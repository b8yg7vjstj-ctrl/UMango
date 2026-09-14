/// Dart ↔ C++ 桥接层的 FFI 绑定与类型化客户端。
///
/// 调用链：[UmangoBridge.invoke] → C++ `umb_bridge_call` → Rust `umango_call`
/// → 核心层分发 → （需要跑命令时）回调 C++ `posix_spawn`。
///
/// 几个必须处理好的工程问题：
///
/// 1. **动态库定位**。阶段1 还没有把 dylib 打进 .app 包（需要改 Xcode 工程），
///    所以这里按固定顺序尝试多个候选路径，全失败时给出**可操作的错误提示**，
///    而不是抛一个看不懂的异常。定位顺序见 [_candidatePaths]。
///
/// 2. **绝不在 UI 线程跑阻塞调用**。每个业务调用都会创建进程、等 CLI 返回，
///    耗时可能到秒级。所有调用都放进 [Isolate.run]，界面不会卡。
///
/// 3. **永远返回结构化结果**。C++/Rust 两侧都保证返回合法 JSON 信封，
///    这里把 `ok=false` 统一翻译成 [UmangoError]，调用方只需处理一种错误类型。
library;

import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:ffi/ffi.dart';

import '../models/umango_models.dart';

// ---------------------------------------------------------------------------
// 动态库定位
// ---------------------------------------------------------------------------

/// 动态库加载失败时抛出，携带已尝试的路径，便于用户自行排查。
class BridgeLoadException implements Exception {
  BridgeLoadException(this.message, this.attemptedPaths, this.lastError);

  final String message;
  final List<String> attemptedPaths;
  final String? lastError;

  @override
  String toString() => '$message\n已尝试的路径:\n${attemptedPaths.map((p) => '  · $p').join('\n')}'
      '${lastError == null ? '' : '\n最后一次错误: $lastError'}';
}

const _libraryFileName = 'libumango_bridge.dylib';

/// 候选路径按优先级排列。
///
/// 之所以有多个候选：阶段1 尚未把 dylib 打进 .app（那需要改 Xcode 工程配置），
/// 开发期直接从构建产物加载。正式打包时应只保留前两项。
List<String> _candidatePaths() {
  final paths = <String>[];

  // 1) 环境变量优先，便于调试与 CI 指定
  final fromEnv = Platform.environment['UMANGO_BRIDGE_LIB'];
  if (fromEnv != null && fromEnv.isNotEmpty) paths.add(fromEnv);

  // 2) 与可执行文件同级（.app/Contents/MacOS/）
  final exeDir = File(Platform.resolvedExecutable).parent.path;
  paths.add('$exeDir/$_libraryFileName');

  // 3) .app/Contents/Frameworks/（Flutter 惯例位置）
  paths.add('$exeDir/../Frameworks/$_libraryFileName');

  // 4) 开发期：原生层构建缓存（scripts/build.sh 的默认输出位置）
  final home = Platform.environment['HOME'];
  if (home != null) {
    paths.add('$home/Library/Caches/umango-build/native/$_libraryFileName');
  }

  // 5) 开发期：仓库内相对路径
  final cwd = Directory.current.path;
  paths.add('$cwd/native/build/$_libraryFileName');
  paths.add('$cwd/../native/build/$_libraryFileName');

  return paths;
}

// ---------------------------------------------------------------------------
// 桥接层
// ---------------------------------------------------------------------------

/// C ABI 函数签名
typedef _CallNative = Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>);
typedef _CallDart = Pointer<Utf8> Function(Pointer<Utf8>, Pointer<Utf8>);
typedef _InitNative = Int32 Function(Pointer<Pointer<Utf8>>);
typedef _InitDart = int Function(Pointer<Pointer<Utf8>>);
typedef _FreeNative = Void Function(Pointer<Utf8>);
typedef _FreeDart = void Function(Pointer<Utf8>);
typedef _StrNative = Pointer<Utf8> Function();
typedef _StrDart = Pointer<Utf8> Function();
typedef _SelfTestNative = Pointer<Utf8> Function();
typedef _SelfTestDart = Pointer<Utf8> Function();
typedef _UintNative = Uint32 Function();
typedef _UintDart = int Function();

/// 桥接层客户端。整个应用共用一个实例。
class UmangoBridge {
  UmangoBridge._(this._library);

  final DynamicLibrary _library;

  static UmangoBridge? _instance;
  static List<String> _attempted = const [];
  static String? _lastError;

  /// 最近一次探测动态库时尝试过的路径（供诊断页展示）
  static List<String> get attemptedPaths => _attempted;
  static String? get lastLoadError => _lastError;

  /// 已加载的桥接层；未初始化时抛出 [BridgeLoadException]。
  static UmangoBridge get instance {
    final existing = _instance;
    if (existing != null) return existing;
    return load();
  }

  /// 是否可以加载（用于启动时决定显示错误页还是主界面）
  static bool get isAvailable => _instance != null;

  /// 尝试加载动态库。
  static UmangoBridge load() {
    final attempted = _candidatePaths();
    _attempted = attempted;
    DynamicLibrary? lib;
    String? lastError;

    for (final path in attempted) {
      try {
        if (!File(path).existsSync()) {
          lastError = '文件不存在';
          continue;
        }
        lib = DynamicLibrary.open(path);
        break;
      } catch (e) {
        lastError = e.toString();
      }
    }

    if (lib == null) {
      _lastError = lastError;
      throw BridgeLoadException(
        '无法加载桥接层动态库 $_libraryFileName。\n'
        '请先构建原生层：执行 scripts/build.sh（会依次构建 Rust 核心层与 C++ 桥接层）。',
        attempted,
        lastError,
      );
    }

    _lastError = null;
    final bridge = UmangoBridge._(lib);
    bridge._initialize();
    _instance = bridge;
    return bridge;
  }

  void _initialize() {
    final init = _library.lookupFunction<_InitNative, _InitDart>('umb_bridge_init');
    final errOut = malloc<Pointer<Utf8>>();
    try {
      final rc = init(errOut);
      if (rc != 0) {
        final message = errOut.value == nullptr ? '未知原因' : errOut.value.toDartString();
        throw StateError('桥接层初始化失败（返回码 $rc）：$message');
      }
    } finally {
      malloc.free(errOut);
    }
  }

  /// ABI 版本
  int get abiVersion =>
      _library.lookupFunction<_UintNative, _UintDart>('umb_bridge_abi_version')();

  /// 核心层版本
  String get coreVersion =>
      _library.lookupFunction<_StrNative, _StrDart>('umb_bridge_core_version')().toDartString();

  /// 宿主平台标识（macos / windows / linux）
  String get platform =>
      _library.lookupFunction<_StrNative, _StrDart>('umb_bridge_platform')().toDartString();

  /// 当前进程执行器。`cpp-bridge` 表示 C++ 进程调用层确实在链路上。
  String get processRunner =>
      _library.lookupFunction<_StrNative, _StrDart>('umb_bridge_runner')().toDartString();

  /// 运行桥接层自检，返回原始 JSON 字符串。
  Future<Map<String, dynamic>> selfTest() async {
    final result = await Isolate.run(() {
      // 注意：新 isolate 里必须重新加载动态库（每个 isolate 有独立的 FFI 缓存），
      // 但底层 dylib 是同一个实例，Rust/C++ 的全局状态是共享的。
      final lib = UmangoBridge.load();
      final fn = lib._library.lookupFunction<_SelfTestNative, _SelfTestDart>(
        'umb_bridge_self_test',
      );
      final ptr = fn();
      try {
        return ptr.toDartString();
      } finally {
        lib._library.lookupFunction<_FreeNative, _FreeDart>('umb_bridge_free')(ptr);
      }
    });
    return jsonDecode(result) as Map<String, dynamic>;
  }

  /// 调用一个核心层方法。
  ///
  /// - [method] 例如 `sources.list` / `packages.versions`
  /// - [args] 会自动 JSON 序列化；传 null 表示无参数
  ///
  /// 返回 `data` 字段的原始 JSON 值。失败时抛出 [UmangoError]。
  Future<dynamic> invoke(String method, [Map<String, dynamic>? args]) async {
    final argsJson = args == null ? '' : jsonEncode(args);

    final envelope = await Isolate.run(() {
      final lib = UmangoBridge.load();
      final call = lib._library.lookupFunction<_CallNative, _CallDart>('umb_bridge_call');
      final free = lib._library.lookupFunction<_FreeNative, _FreeDart>('umb_bridge_free');

      final methodPtr = method.toNativeUtf8();
      final argsPtr = argsJson.toNativeUtf8();
      try {
        final resultPtr = call(methodPtr, argsPtr);
        try {
          return resultPtr.toDartString();
        } finally {
          free(resultPtr);
        }
      } finally {
        malloc.free(methodPtr);
        malloc.free(argsPtr);
      }
    });

    final decoded = jsonDecode(envelope);
    if (decoded is! Map<String, dynamic>) {
      throw const UmangoError(
        code: 'bad_envelope',
        message: '核心层返回的信封不是 JSON 对象',
        advice: RecoveryAdvice.manual,
        retryable: false,
      );
    }
    if (decoded['ok'] != true) {
      throw UmangoError.fromJson(
        (decoded['error'] as Map?)?.cast<String, dynamic>() ?? const {},
      );
    }
    return decoded['data'];
  }

  // -------------------------------------------------------------------------
  // 类型化 API —— 把 JSON 契约收敛到一处，界面层不再直接碰 Map
  // -------------------------------------------------------------------------

  Future<CoreInfo> coreInfo() async =>
      CoreInfo.fromJson(_asMap(await invoke('core.info')) ?? const {});

  /// 包源列表（含平台门控与可用性）。
  Future<List<SourceView>> sources({bool refresh = false}) async {
    final data = await invoke(refresh ? 'sources.refresh' : 'sources.list');
    return _asList(data).map((e) => SourceView.fromJson(_asMap(e)!)).toList();
  }

  Future<List<PackageMeta>> search(
    PackageSourceKind source,
    String query, {
    int limit = 24,
  }) async {
    final data = await invoke('packages.search', {
      'source': source.id,
      'query': query,
      'limit': limit,
    });
    return _asList(data).map((e) => PackageMeta.fromJson(_asMap(e)!)).toList();
  }

  Future<List<PackageVersion>> versions(PackageSourceKind source, String name) async {
    final data = await invoke('packages.versions', {
      'source': source.id,
      'name': name,
    });
    return _asList(data).map((e) => PackageVersion.fromJson(_asMap(e)!)).toList();
  }

  Future<DependencyTree> dependencies(
    PackageSourceKind source,
    String name, {
    String? version,
    int maxDepth = 3,
    bool includeOptionalKinds = false,
  }) async {
    final data = await invoke('packages.dependencies', {
      'source': source.id,
      'name': name,
      if (version != null) 'version': version,
      'max_depth': maxDepth,
      'include_optional_kinds': includeOptionalKinds,
    });
    return DependencyTree.fromJson(_asMap(data)!);
  }

  Future<DownloadStats> downloads(
    PackageSourceKind source,
    String name, {
    String? repository,
  }) async {
    final data = await invoke('packages.downloads', {
      'source': source.id,
      'name': name,
      if (repository != null) 'repository': repository,
    });
    return DownloadStats.fromJson(_asMap(data));
  }

  /// 把"安装"解析成一条可执行命令（阶段1 只展示，不执行）。
  Future<InstallPlan> resolveInstall(
    PackageSourceKind source,
    String name, {
    String? version,
    InstallScope? scope,
  }) async {
    final data = await invoke('packages.resolve_install', {
      'source': source.id,
      'name': name,
      if (version != null) 'version': version,
      if (scope != null) 'scope': _scopeId(scope),
    });
    return InstallPlan.fromJson(_asMap(_asMap(data)?['plan']) ?? const {});
  }
}

String _scopeId(InstallScope scope) => switch (scope) {
      InstallScope.global => 'global',
      InstallScope.projectLocal => 'project_local',
      InstallScope.userSystem => 'user_system',
      InstallScope.machineSystem => 'machine_system',
    };

Map<String, dynamic>? _asMap(dynamic v) =>
    v is Map<String, dynamic> ? v : (v is Map ? Map<String, dynamic>.from(v) : null);

List<dynamic> _asList(dynamic v) => v is List ? v : const [];
