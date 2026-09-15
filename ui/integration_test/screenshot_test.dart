/// UMango 集成测试：在**真实运行的 macOS 应用**里抓取界面截图到磁盘。
///
/// 目的：为「界面 1:1 还原度」提供客观校验素材 —— 由 `scripts/compare_screenshots.py`
/// 对产出的 PNG 做纯程序化像素测量，与 PCL-CE 的参照数值比对。
///
/// 运行方式（**必须**走 drive）：
/// ```
/// cd "$P/ui"
/// flutter drive \
///   --driver=test_driver/integration_test.dart \
///   --target=integration_test/screenshot_test.dart \
///   -d macos
/// ```
/// 输出：`$P/ui/build/screenshots/umango_{home,download,settings,tools}.png`
///
/// ---------------------------------------------------------------------------
/// 抓帧方式：两条路，主力是第二条
/// ---------------------------------------------------------------------------
/// **路 A（标准写法，本机不可用）**：`binding.takeScreenshot('name')`。
/// 它把截图请求转成 `plugins.flutter.io/integration_test` 通道上的
/// `captureScreenshot` 调用（`integration_test/lib/src/_callback_io.dart`），
/// 由原生侧真正抓屏并把字节交给 `flutter drive` 的 `onScreenshot` 回调。
///
/// **已核实的原因**：上游 `integration_test_macos` 的 `IntegrationTestPlugin.swift`
/// 只实现了 `allTestsFinished`，`default` 分支返回 `FlutterMethodNotImplemented`；
/// 而且它的 `pubspec.yaml` 没有声明 `macos` 平台，所以本项目解析出的
/// `.flutter-plugins-dependencies` 里 `macos` 数组为空、
/// `GeneratedPluginRegistrant.swift` 也不会注册这个插件。
/// 因此本机 macOS 上 `takeScreenshot()` **必然**失败（MissingPluginException）。
/// 代码里仍然按任务要求调用它一次（`try` 包住），把真实异常记进测试日志，
/// 同时也为将来上游补齐 macOS 实现留好接线。
///
/// **路 B（本机实际使用）**：直接在应用进程里从渲染树取帧。
/// `RenderView.layer` 是根 `OffsetLayer`，`toImage()` 把它光栅化成
/// `ui.Image`，再编成 PNG 写盘。这条路拿到的是**应用真实渲染的那一帧**
/// （同一个 layer tree、同一个光栅化路径），只依赖 `dart:ui`，
/// 不需要任何平台插件、不需要 Chrome、不经过视觉模型。
/// 实现见 [_captureFramePng]。
///
/// ---------------------------------------------------------------------------
/// 为什么列表页要等：首页/下载页在 `initState` 里真实调用核心层
/// （`core.info` / `sources.list`，后者会真的 `--version` 探测 npm/pnpm/git/brew），
/// 所以必须用「循环 pump + 真实 Future.delayed」等真实时间，不能只 pump 帧。
/// ---------------------------------------------------------------------------
library;

// 截图测试的诊断输出就是给 CI/人工看日志用的，print 是这里的正确工具。
// ignore_for_file: avoid_print

import 'dart:async';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart' show EditableText;
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:umango/main.dart';
import 'package:umango/widgets/package_card.dart';
import 'package:umango/widgets/pcl_widgets.dart';

// ---------------------------------------------------------------------------
// 等待/路径工具
// ---------------------------------------------------------------------------

/// 截图输出目录：`<包目录>/build/screenshots`。
///
/// 不用 `compile-time` 常量写死绝对路径 —— 包目录包含空格与非 ASCII
/// （OneDrive 挂载点会不定时改名），一切路径都在运行期解析。
Directory screenshotOutputDirectory() => Directory(
  '${Directory.current.path}${Platform.pathSeparator}build'
  '${Platform.pathSeparator}screenshots',
);

/// 真实等待 [duration]：集成测试里定时器是真的，所以每轮 pump 之间
/// 都插一个真实的 `Future.delayed`，让异步 IO（FFI 调用、CLI 探测）真的推进。
Future<void> _realDelay(WidgetTester tester, Duration duration) async {
  final DateTime end = DateTime.now().add(duration);
  while (DateTime.now().isBefore(end)) {
    await tester.pump(const Duration(milliseconds: 100));
    await Future<void>.delayed(const Duration(milliseconds: 50));
  }
}

/// 迭代泵帧直到 [ready] 成立，或超过 [timeout] / [maxRounds]。
///
/// 返回是否在超时前达成，调用方据此决定是否继续（不达成就写日志，不静默等待）。
Future<bool> _pumpUntil(
  WidgetTester tester,
  bool Function() ready, {
  Duration timeout = const Duration(seconds: 12),
  Duration step = const Duration(milliseconds: 200),
  int maxRounds = 60,
}) async {
  final DateTime deadline = DateTime.now().add(timeout);
  for (int round = 0; round < maxRounds; round++) {
    if (ready()) return true;
    if (!DateTime.now().isBefore(deadline)) return false;
    await tester.pump(step);
    await Future<void>.delayed(const Duration(milliseconds: 60));
  }
  return ready();
}

// ---------------------------------------------------------------------------
// 抓帧
// ---------------------------------------------------------------------------

/// 把当前渲染树光栅化成 PNG 字节。
///
/// `RenderView.layer` 是整帧的根 `OffsetLayer`；`toImage()` 给出的是
/// **逻辑像素尺寸**的图像（`RenderView` 的 size 即逻辑尺寸），因此产出的 PNG
/// 在 macOS 上通常等于逻辑尺寸 —— 具体缩放比由 `compare_screenshots.py`
/// 从图片宽度反推并在报告里说明。
///
/// `toByteData` 是真正的异步引擎调用，必须放在 `tester.runAsync` 里，
/// 否则在 live binding 的帧内同步区会死等。
Future<Uint8List?> _captureFramePng(WidgetTester tester) async {
  final RenderView? view = RendererBinding.instance.renderViews.isEmpty
      ? null
      : RendererBinding.instance.renderViews.first;
  if (view == null) {
    print('[shot] ✗ 渲染树里没有 RenderView，无法取帧');
    return null;
  }

  /*
   * 用 debugLayer 而不是 layer：`RenderObject.layer` 是 @protected，
   * 从外部访问会触发 invalid_use_of_protected_member（即便只是读取）。
   * `debugLayer` 是公开的调试用访问器，debug 构建下返回同一棵树里的 layer
   * （集成测试就是 debug 构建），release 下返回 null —— 上面已经判空。
   */
  final ContainerLayer? rootLayer = view.debugLayer;
  if (rootLayer is! OffsetLayer) {
    print(
      '[shot] ✗ RenderView.debugLayer 不是 OffsetLayer（${rootLayer.runtimeType}），'
      '无法取帧',
    );
    return null;
  }

  final ui.Image image = await rootLayer.toImage(view.paintBounds);

  Uint8List? png;
  await tester.runAsync(() async {
    final ByteData? data = await image.toByteData(
      format: ui.ImageByteFormat.png,
    );
    png = data?.buffer.asUint8List();
  });
  image.dispose();
  return png;
}

/// 在**独立的 error zone** 里调用 `binding.takeScreenshot`。
///
/// 为什么必须额外包一层 zone（实测踩到的坑，记下来免得后人重踩）：
/// macOS 上原生没实现 `captureScreenshot`，方法通道返回 MissingPluginException；
/// 这个异常是从平台消息回调里抛出来的**异步未捕获异常**，`try/catch` 抓不到它，
/// 它会逃到根 zone 把整个 Dart isolate 打死 —— 第一次跑 `flutter drive` 时
/// 日志里就是：截图写盘成功 → 打印 MissingPluginException → 紧接着
/// `ext.flutter.driver: (112) Service has disappeared`，后面三页再也截不到。
///
/// `runZonedGuarded` 提供的子 zone 有自己的 `handleUncaughtError`，
/// 异常在源头的 zone 就被接住，不再往上冒。
Future<void> _tryStandardDriverScreenshot(
  IntegrationTestWidgetsFlutterBinding binding,
  String name,
) async {
  final Completer<void> done = Completer<void>();
  runZonedGuarded<void>(
    () async {
      try {
        final List<int> bytes = await binding.takeScreenshot(name);
        print(
          '[shot] ✓ binding.takeScreenshot("$name") 成功，${bytes.length} 字节'
          '（本平台原生实现了 captureScreenshot）',
        );
      } catch (e) {
        print('[shot] ✗ binding.takeScreenshot("$name") 同步抛出：$e');
      } finally {
        if (!done.isCompleted) done.complete();
      }
    },
    (Object error, StackTrace stack) {
      print(
        '[shot] ✗ binding.takeScreenshot("$name") 异步失败（已被 zone 接住，'
        '应用不会因此退出）：$error',
      );
      if (!done.isCompleted) done.complete();
    },
  );
  // 平台消息往返很快；给 5 秒上限，避免万一没有回调时卡住整个截图流程。
  await done.future.timeout(
    const Duration(seconds: 5),
    onTimeout: () =>
        print('[shot] binding.takeScreenshot("$name") 5 秒内没有回调，放弃等待'),
  );
}

/// 抓帧 → 写盘 → 再试一次标准 `binding.takeScreenshot`。
///
/// 顺序刻意是「先写盘、后调通道」：通道这条路在本机 macOS 上必然失败
/// （原因见文件头），先写盘能保证主产物一定落下来。
Future<String?> _captureAndSave(
  WidgetTester tester,
  IntegrationTestWidgetsFlutterBinding binding,
  String name,
  Directory out, {
  bool tryStandardShot = true,
}) async {
  // 截图前 pump 若干帧，确保最后一次 setState/布局已经落到 layer 上。
  for (int i = 0; i < 5; i++) {
    await tester.pump(const Duration(milliseconds: 60));
  }

  final String? path = await _captureAndSaveFrame(tester, name, out);

  if (tryStandardShot) {
    await _tryStandardDriverScreenshot(binding, name);
    print('[shot]   → 应用内 RenderView.debugLayer.toImage() 兜底已产出 $path');
  }

  return path;
}

/// 只做「取帧 + 写盘」，不碰平台通道。
Future<String?> _captureAndSaveFrame(
  WidgetTester tester,
  String name,
  Directory out,
) async {
  Uint8List? png;
  try {
    png = await _captureFramePng(tester);
  } catch (e) {
    print('[shot] ✗ 取帧抛异常（$name）：$e');
    return null;
  }
  if (png == null || png.isEmpty) {
    print('[shot] ✗ 取帧返回空字节（$name）');
    return null;
  }

  try {
    await out.create(recursive: true);
  } catch (e) {
    print('[shot] ✗ 无法创建输出目录 ${out.path}：$e');
    return null;
  }

  final File file = File('${out.path}${Platform.pathSeparator}$name.png');
  try {
    await file.writeAsBytes(png, flush: true);
  } catch (e) {
    print('[shot] ✗ 写盘失败 ${file.path}：$e');
    return null;
  }

  print(
    '[shot] ✓ $name → ${file.path} '
    '(${png.length} 字节 = ${(png.length / 1024).toStringAsFixed(1)} KiB)',
  );
  return file.path;
}

// ---------------------------------------------------------------------------
// 测试
// ---------------------------------------------------------------------------

void main() {
  final IntegrationTestWidgetsFlutterBinding binding =
      IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  /// 已落盘的文件路径，最后统一汇总（便于脚本/报告读取）。
  final List<String> saved = <String>[];

  testWidgets('截取 UMango 四个页面的真实渲染画面', (WidgetTester tester) async {
    _installLastResortErrorHandler();

    /*
     * 整个测试体跑在自定义 error zone 里，作为最后一道保险：
     * 任何漏网的异步未捕获异常都不会打死 isolate，而是变成一条日志。
     * 之所以需要：实测发现 macOS 上 `captureScreenshot` 的
     * MissingPluginException 是异步抛出的，会把 Dart 侧进程整个带走
     * （见 [_tryStandardDriverScreenshot] 的注释）。
     * 异常本身已经被里面的子 zone 接住；这里再兜一层，
     * 保证「截不到第 2 页」不会退化成「一页都留不下来」。
     */
    final Completer<void> zoneDone = Completer<void>();
    runZonedGuarded<void>(
      () async {
        try {
          await _runScreenshotScenario(tester, binding, saved);
        } finally {
          if (!zoneDone.isCompleted) zoneDone.complete();
        }
      },
      (Object error, StackTrace stack) {
        print('[shot] !! 兜底 zone 接到未捕获异常（应用继续）：$error');
        if (!zoneDone.isCompleted) zoneDone.complete();
      },
    );
    await zoneDone.future;
  });
}

/// 在下载页做一次真实搜索，让列表里出现包卡片。
///
/// 为什么需要它：参照值「列表行高 64」只有在**有卡片**时才可测；空态下没有行，
/// `compare_screenshots.py` 会如实报告"无法测量"。这里真的往搜索框里打字、
/// 真的点「搜索」按钮，因此会触发核心层的 npm 搜索（真实联网）。
/// 整个过程是 best-effort：找不到控件、或搜索失败（离线）都不让测试失败，
/// 只是截到的仍是空态，脚本照实报"无法测量"。
Future<void> _runDownloadSearch(WidgetTester tester) async {
  const String query = 'express';

  final Finder field = find.byType(EditableText);
  final Finder button = find.widgetWithText(PclButton, '搜索');
  if (field.evaluate().isEmpty || button.evaluate().isEmpty) {
    print('[shot] 下载页搜索：找不到输入框或「搜索」按钮，跳过（截图仍是空态）');
    return;
  }

  try {
    await tester.tap(field.first);
    await tester.enterText(field.first, query);
    await tester.pump(const Duration(milliseconds: 120));
    await tester.tap(button.first);
    print('[shot] 下载页搜索：已提交查询 "$query"，等待真实网络返回…');
  } catch (e) {
    print('[shot] 下载页搜索：输入/点击抛异常，跳过：$e');
    return;
  }

  // 真实联网搜索：轮询等待卡片出现，最多 20 秒。
  final DateTime deadline = DateTime.now().add(const Duration(seconds: 20));
  bool found = false;
  while (DateTime.now().isBefore(deadline)) {
    await tester.pump(const Duration(milliseconds: 150));
    await Future<void>.delayed(const Duration(milliseconds: 150));
    if (find.byType(PackageCard).evaluate().isNotEmpty) {
      found = true;
      break;
    }
  }
  print(
    '[shot] 下载页搜索：${found ? "已渲染出包卡片" : "20 秒内没有卡片"
              "（可能离线 / 搜索失败，截图仍是空态/错误态）"}',
  );
  // 再等一会儿让入场动画结束，卡片位置稳定下来。
  await _realDelay(tester, const Duration(seconds: 2));
}

/// 兜底错误处理：把 FlutterError 转成日志而不是让进程退出。
void _installLastResortErrorHandler() {
  final FlutterExceptionHandler? old = FlutterError.onError;
  FlutterError.onError = (FlutterErrorDetails details) {
    print('[shot] !! FlutterError 被兜底捕获：${details.exceptionAsString()}');
    if (old != null) old(details);
  };
}

/// 真正的截图流程。
Future<void> _runScreenshotScenario(
  WidgetTester tester,
  IntegrationTestWidgetsFlutterBinding binding,
  List<String> saved,
) async {
  final Directory out = screenshotOutputDirectory();
  print('[shot] 输出目录：${out.absolute.path}');
  print('[shot] 进程 CWD：${Directory.current.path}');

  /*
     * 固定测试视口（只改测试内的视图配置，不动 ui/lib 下任何代码）。
     *
     * 为什么显式钉住而不是用真实窗口尺寸：真实窗口由
     * macos/Runner/Base.lproj/MainMenu.xib 的 contentRect 决定（800×600 逻辑），
     * 而 Mac 上多为 Retina（devicePixelRatio = 2），逻辑尺寸会随显示器变化，
     * 于是「截图宽度 → 缩放比」的推断在换机器后就不成立了。
     * 这里设成 DPR = 1.0、物理 1280×800 ⇒ 逻辑 1280×800，
     * 产出的 PNG 宽度就等于逻辑宽度，缩放比恒为 1.0，测量口径稳定可复现。
     * 视口够大，下载页的「左栏 168 + 内容区」不会被挤压。
     */
  tester.view.devicePixelRatio = 1.0;
  tester.view.physicalSize = const Size(1280, 800);
  addTearDown(tester.view.reset);

  // ---- 1. 挂载应用 ------------------------------------------------------
  await tester.pumpWidget(const UmangoApp());
  await tester.pump();

  final RenderView firstView = RendererBinding.instance.renderViews.first;
  print(
    '[shot] 视图：logical size = ${firstView.size}，'
    'png 尺寸将等于该逻辑尺寸',
  );

  // ---- 2. 等桥接层加载 --------------------------------------------------
  final bool bridged = await _pumpUntil(
    tester,
    () => find.text('正在加载核心层…').evaluate().isEmpty,
    timeout: const Duration(seconds: 20),
  );
  print('[shot] 桥接层闸门${bridged ? '已放行（进入主界面或诊断页）' : '等待超时（仍在加载页）'}');
  // 首页数据加载：core.info + sources.list（后者真的探测 CLI），需要真实时间。
  await _realDelay(tester, const Duration(seconds: 7));

  final bool homeReady = await _pumpUntil(
    tester,
    () => find.text('主页').evaluate().isNotEmpty,
  );
  print('[shot] 首页标签${homeReady ? '已出现' : '未出现（可能停在桥接诊断页）'}');

  // ---- 3. 主页 ----------------------------------------------------------
  await _realDelay(tester, const Duration(seconds: 3));
  final String? home = await _captureAndSave(
    tester,
    binding,
    'umango_home',
    out,
  );
  if (home != null) saved.add(home);

  // ---- 4. 下载 / 设置 / 工具 -------------------------------------------
  const Map<String, String> tabs = <String, String>{
    '下载': 'umango_download',
    '设置': 'umango_settings',
    '工具': 'umango_tools',
  };

  for (final MapEntry<String, String> tab in tabs.entries) {
    final Finder target = find.text(tab.key);
    if (target.evaluate().isEmpty) {
      print(
        '[shot] ✗ 找不到标签「${tab.key}」，跳过 ${tab.value}'
        '（应用可能停在桥接诊断页）',
      );
      continue;
    }

    await tester.tap(target.first);
    // 切页动效 + 新页面 initState 里的真实数据加载：给足 4 秒。
    await _realDelay(tester, const Duration(seconds: 4));

    // 下载页额外做一次真实搜索 —— "列表行高 64" 这条参照只有在有卡片时才测得到，
    // 空态下没有行，脚本会如实报"无法测量"。这里真的输入 + 真的点搜索按钮，
    // 触发核心层的 npm 搜索（真实联网），因此给足 12 秒并允许失败。
    if (tab.key == '下载') {
      await _runDownloadSearch(tester);
    }

    final String? path = await _captureAndSave(tester, binding, tab.value, out);
    if (path != null) saved.add(path);
  }

  // ---- 5. 汇总 ----------------------------------------------------------
  print('[shot] ===== 共落盘 ${saved.length} 张截图 =====');
  for (final String path in saved) {
    print('[shot]   · $path');
  }
  // 不给视觉模型留任何依赖：这些 PNG 只供 scripts/compare_screenshots.py
  // 做程序化像素测量。
  expect(
    saved,
    isNotEmpty,
    reason:
        '一张截图都没落盘：请检查 build/screenshots 是否可写、'
        '以及应用是否卡在桥接层加载页',
  );
}
