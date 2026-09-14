/// UMango 前端数据模型。
///
/// 这些类与 Rust 核心层 `umango-core::model` 一一对应，字段名就是 JSON 契约
/// （见 `core/crates/umango-core/src/model.rs`）。改动字段名等于破坏前后端契约，
/// 必须两边同时改。
///
/// 解析策略：**宽容解析**。任何一个字段缺失或类型不符都不应该让整个界面崩掉，
/// 因此所有取值都走带默认值的辅助函数，并在解析失败处保留降级行为。
library;

import 'package:flutter/foundation.dart';

// ---------------------------------------------------------------------------
// 基础枚举
// ---------------------------------------------------------------------------

/// 包源种类。对应需求里的源列表（npm / git / scoop / winget / pnpm + Homebrew）。
enum PackageSourceKind {
  npm('npm', 'npm', '#CB3837'),
  pnpm('pnpm', 'pnpm', '#F69220'),
  git('git', 'Git', '#F05032'),
  scoop('scoop', 'Scoop', '#5D5D5D'),
  winget('winget', 'WinGet', '#0078D4'),
  homebrew('homebrew', 'Homebrew', '#FBB040');

  const PackageSourceKind(this.id, this.displayName, this.accentHex);

  /// 与 Rust 对应的标识字符串
  final String id;
  final String displayName;

  /// 品牌主色（卡片强调色）
  final String accentHex;

  static PackageSourceKind? tryParse(String? raw) {
    if (raw == null) return null;
    for (final k in PackageSourceKind.values) {
      if (k.id == raw) return k;
    }
    return null;
  }
}

/// 版本通道：与 PCL-CE 的「正式版 / 快照 / 测试版」区分对应。
enum ReleaseChannel {
  stable('正式版'),
  prerelease('测试版'),
  snapshot('快照'),
  unknown('未知通道');

  const ReleaseChannel(this.displayName);
  final String displayName;

  static ReleaseChannel tryParse(String? raw) => switch (raw) {
        'stable' => ReleaseChannel.stable,
        'prerelease' => ReleaseChannel.prerelease,
        'snapshot' => ReleaseChannel.snapshot,
        _ => ReleaseChannel.unknown,
      };
}

/// 安装作用域
enum InstallScope {
  global('全局'),
  projectLocal('项目本地'),
  userSystem('当前用户'),
  machineSystem('整机');

  const InstallScope(this.displayName);
  final String displayName;

  static InstallScope tryParse(String? raw) => switch (raw) {
        'global' => InstallScope.global,
        'project_local' => InstallScope.projectLocal,
        'user_system' => InstallScope.userSystem,
        'machine_system' => InstallScope.machineSystem,
        _ => InstallScope.global,
      };
}

/// 依赖类型
enum DependencyKind {
  runtime('运行时依赖'),
  dev('开发依赖'),
  peer('同级依赖'),
  optional('可选依赖'),
  bundled('内置依赖');

  const DependencyKind(this.displayName);
  final String displayName;

  static DependencyKind tryParse(String? raw) => switch (raw) {
        'dev' => DependencyKind.dev,
        'peer' => DependencyKind.peer,
        'optional' => DependencyKind.optional,
        'bundled' => DependencyKind.bundled,
        _ => DependencyKind.runtime,
      };
}

/// 包源可用性状态。UI 据此决定入口是否隐藏 / 是否提示安装 CLI。
enum AvailabilityState {
  available('可用'),
  degraded('可用（数据可能过期）'),
  unsupportedPlatform('当前平台不支持'),
  notInstalled('未安装'),
  unreachable('源不可达');

  const AvailabilityState(this.displayName);
  final String displayName;

  static AvailabilityState tryParse(String? raw) => switch (raw) {
        'available' => AvailabilityState.available,
        'degraded' => AvailabilityState.degraded,
        'unsupported_platform' => AvailabilityState.unsupportedPlatform,
        'not_installed' => AvailabilityState.notInstalled,
        _ => AvailabilityState.unreachable,
      };

  bool get isUsable =>
      this == AvailabilityState.available || this == AvailabilityState.degraded;
}

/// 依赖节点状态 —— 依赖故障的兜底分支都在这里体现。
enum DependencyState { resolved, unverified, missing, conflict, cyclic, truncated }

// ---------------------------------------------------------------------------
// 下载量：官方优先，GitHub Stars 兜底
// ---------------------------------------------------------------------------

/// 下载量统计。
///
/// 关键设计：**「拿不到」与「是 0」是两回事**。
/// 拿不到时是 [UnknownDownloadStats]，界面显示"暂无数据"并给出原因，
/// 绝不显示成 0 误导用户。
sealed class DownloadStats {
  const DownloadStats();

  static DownloadStats fromJson(Map<String, dynamic>? json) {
    if (json == null) return UnknownDownloadStats('核心层未返回下载量');
    switch (json['kind'] as String?) {
      case 'official':
        return OfficialDownloadStats(
          count: _asInt(json['count']) ?? 0,
          period: _periodName(json['period'] as String?),
          via: json['via'] as String? ?? '未知来源',
        );
      case 'github_stars':
        return GitHubStarsStats(
          count: _asInt(json['count']) ?? 0,
          repo: json['repo'] as String? ?? '',
        );
      default:
        return UnknownDownloadStats(json['reason'] as String? ?? '未知原因');
    }
  }

  /// 指标数值；拿不到时为 null。
  int? get count;

  /// 指标名称。GitHub Stars 不是下载量，必须如实标注。
  String get metricName;

  /// 数据来源说明
  String get sourceLabel;

  /// 是否是兜底数据
  bool get isFallback;
}

class OfficialDownloadStats extends DownloadStats {
  const OfficialDownloadStats({
    required this.count,
    required this.period,
    required this.via,
  });

  @override
  final int count;
  final String period;
  final String via;

  @override
  String get metricName => '下载量';
  @override
  String get sourceLabel => '$via · $period';
  @override
  bool get isFallback => false;
}

class GitHubStarsStats extends DownloadStats {
  const GitHubStarsStats({required this.count, required this.repo});

  @override
  final int count;
  final String repo;

  @override
  String get metricName => 'GitHub Stars';
  @override
  String get sourceLabel => 'github.com/$repo';
  @override
  bool get isFallback => true;
}

class UnknownDownloadStats extends DownloadStats {
  const UnknownDownloadStats(this.reason);

  final String reason;

  @override
  int? get count => null;
  @override
  String get metricName => '暂无数据';
  @override
  String get sourceLabel => reason;
  @override
  bool get isFallback => false;
}

// ---------------------------------------------------------------------------
// 包源视图
// ---------------------------------------------------------------------------

class SourceCapabilities {
  const SourceCapabilities({
    required this.search,
    required this.listVersions,
    required this.resolveDeps,
    required this.officialDownloads,
    required this.install,
    required this.uninstall,
    required this.upgrade,
  });

  final bool search;
  final bool listVersions;
  final bool resolveDeps;
  final bool officialDownloads;
  final bool install;
  final bool uninstall;
  final bool upgrade;

  factory SourceCapabilities.fromJson(Map<String, dynamic>? j) => SourceCapabilities(
        search: j?['search'] == true,
        listVersions: j?['list_versions'] == true,
        resolveDeps: j?['resolve_deps'] == true,
        officialDownloads: j?['official_downloads'] == true,
        install: j?['install'] == true,
        uninstall: j?['uninstall'] == true,
        upgrade: j?['upgrade'] == true,
      );
}

class SourceAvailability {
  const SourceAvailability({
    required this.state,
    required this.cliFound,
    this.cliPath,
    this.cliVersion,
    this.checkedAt,
    this.stale = false,
    this.detail,
  });

  final AvailabilityState state;
  final bool cliFound;
  final String? cliPath;
  final String? cliVersion;
  final String? checkedAt;

  /// 数据是否来自过期缓存（24 小时保护降级后的结果）
  final bool stale;
  final String? detail;

  factory SourceAvailability.fromJson(Map<String, dynamic>? j) => SourceAvailability(
        state: AvailabilityState.tryParse(j?['state'] as String?),
        cliFound: j?['cli_found'] == true,
        cliPath: j?['cli_path'] as String?,
        cliVersion: j?['cli_version'] as String?,
        checkedAt: j?['checked_at'] as String?,
        stale: j?['stale'] == true,
        detail: j?['detail'] as String?,
      );
}

/// 包源列表项 —— 前端「包源列表」直接渲染这个。
class SourceView {
  const SourceView({
    required this.kind,
    required this.displayName,
    required this.iconKey,
    required this.accentHex,
    required this.cliName,
    required this.defaultScope,
    required this.capabilities,
    required this.supportedHere,
    required this.availability,
    this.verificationNote,
  });

  final PackageSourceKind kind;
  final String displayName;
  final String iconKey;
  final String accentHex;
  final String cliName;
  final InstallScope defaultScope;
  final SourceCapabilities capabilities;

  /// false 时界面必须隐藏该包源入口（平台能力检测的结果）
  final bool supportedHere;
  final SourceAvailability availability;

  /// 诚实标注：解析逻辑是否已在真机验证过。null 表示已验证。
  final String? verificationNote;

  factory SourceView.fromJson(Map<String, dynamic> j) {
    final kind = PackageSourceKind.tryParse(j['kind'] as String?) ?? PackageSourceKind.npm;
    return SourceView(
      kind: kind,
      displayName: j['display_name'] as String? ?? kind.displayName,
      iconKey: j['icon_key'] as String? ?? 'icon_${kind.id}',
      accentHex: j['accent'] as String? ?? kind.accentHex,
      cliName: j['cli_name'] as String? ?? kind.id,
      defaultScope: InstallScope.tryParse(j['default_scope'] as String?),
      capabilities: SourceCapabilities.fromJson(_asMap(j['capabilities'])),
      supportedHere: j['supported_here'] == true,
      availability: SourceAvailability.fromJson(_asMap(j['availability'])),
      verificationNote: j['verification_note'] as String?,
    );
  }

  /// 能否在界面上展示为"可用入口"
  bool get isSelectable => supportedHere && availability.state.isUsable;
}

// ---------------------------------------------------------------------------
// 包与版本
// ---------------------------------------------------------------------------

class InstalledState {
  const InstalledState({
    required this.version,
    required this.outdated,
    this.latest,
  });

  final String version;
  final bool outdated;
  final String? latest;

  factory InstalledState.fromJson(Map<String, dynamic> j) => InstalledState(
        version: j['version'] as String? ?? '未知',
        outdated: j['outdated'] == true,
        latest: j['latest'] as String?,
      );
}

/// 包元数据 —— 对应界面上的「下载卡片」。
class PackageMeta {
  const PackageMeta({
    required this.source,
    required this.name,
    required this.displayName,
    required this.latestChannel,
    required this.downloads,
    this.summary,
    this.homepage,
    this.repository,
    this.license,
    this.latestVersion,
    this.updatedAt,
    this.keywords = const [],
    this.installed,
  });

  final PackageSourceKind source;
  final String name;
  final String displayName;
  final String? summary;
  final String? homepage;
  final String? repository;
  final String? license;
  final String? latestVersion;
  final ReleaseChannel latestChannel;
  final DownloadStats downloads;
  final List<String> keywords;
  final String? updatedAt;
  final InstalledState? installed;

  String get qualifiedId => '${source.id}:$name';

  factory PackageMeta.fromJson(Map<String, dynamic> j) {
    final id = _asMap(j['id']) ?? const {};
    final source = PackageSourceKind.tryParse(id['source'] as String?) ?? PackageSourceKind.npm;
    final installed = _asMap(j['installed']);
    return PackageMeta(
      source: source,
      name: id['name'] as String? ?? j['display_name'] as String? ?? '未知包',
      displayName: j['display_name'] as String? ?? id['name'] as String? ?? '未知包',
      summary: _emptyToNull(j['summary'] as String?),
      homepage: _emptyToNull(j['homepage'] as String?),
      repository: _emptyToNull(j['repository'] as String?),
      license: _emptyToNull(j['license'] as String?),
      latestVersion: _emptyToNull(j['latest_version'] as String?),
      latestChannel: ReleaseChannel.tryParse(j['latest_channel'] as String?),
      downloads: DownloadStats.fromJson(_asMap(j['downloads'])),
      keywords: _asStringList(j['keywords']),
      updatedAt: _emptyToNull(j['updated_at'] as String?),
      installed: installed == null ? null : InstalledState.fromJson(installed),
    );
  }
}

class DependencyRequirement {
  const DependencyRequirement({
    required this.name,
    required this.requirement,
    required this.kind,
  });

  final String name;
  final String requirement;
  final DependencyKind kind;

  factory DependencyRequirement.fromJson(Map<String, dynamic> j) => DependencyRequirement(
        name: j['name'] as String? ?? '?',
        requirement: j['requirement'] as String? ?? '*',
        kind: DependencyKind.tryParse(j['kind'] as String?),
      );
}

/// 单个版本。版本列表页按通道分组展示。
class PackageVersion {
  const PackageVersion({
    required this.version,
    required this.channel,
    this.publishedAt,
    this.deprecated = false,
    this.note,
    this.requires = const [],
  });

  final String version;
  final ReleaseChannel channel;
  final String? publishedAt;
  final bool deprecated;
  final String? note;
  final List<DependencyRequirement> requires;

  factory PackageVersion.fromJson(Map<String, dynamic> j) => PackageVersion(
        version: j['version'] as String? ?? '?',
        channel: ReleaseChannel.tryParse(j['channel'] as String?),
        publishedAt: _emptyToNull(j['published_at'] as String?),
        deprecated: j['deprecated'] == true,
        note: _emptyToNull(j['note'] as String?),
        requires: (_asList(j['requires']))
            .whereType<Map<String, dynamic>>()
            .map(DependencyRequirement.fromJson)
            .toList(),
      );
}

// ---------------------------------------------------------------------------
// 依赖树
// ---------------------------------------------------------------------------

class DependencyNode {
  const DependencyNode({
    required this.source,
    required this.name,
    required this.requirement,
    required this.kind,
    required this.state,
    required this.downloads,
    required this.depth,
    this.resolved,
    this.reason,
    this.children = const [],
  });

  final PackageSourceKind source;
  final String name;
  final String requirement;
  final String? resolved;
  final DependencyKind kind;
  final DependencyState state;

  /// 未验证/冲突时的原因说明
  final String? reason;
  final DownloadStats downloads;
  final int depth;
  final List<DependencyNode> children;

  String get qualifiedId => '${source.id}:$name';

  /// 显示用的版本：已解析显示具体版本，否则显示声明的范围
  String get displayVersion => resolved ?? requirement;

  bool get hasProblem => state != DependencyState.resolved;

  factory DependencyNode.fromJson(Map<String, dynamic> j) {
    final id = _asMap(j['id']) ?? const {};
    final status = _asMap(j['status']) ?? const {};
    final stateRaw = status['state'] as String?;
    return DependencyNode(
      source: PackageSourceKind.tryParse(id['source'] as String?) ?? PackageSourceKind.npm,
      name: id['name'] as String? ?? '?',
      requirement: j['requirement'] as String? ?? '*',
      resolved: _emptyToNull(j['resolved'] as String?),
      kind: DependencyKind.tryParse(j['kind'] as String?),
      state: switch (stateRaw) {
        'resolved' => DependencyState.resolved,
        'unverified' => DependencyState.unverified,
        'missing' => DependencyState.missing,
        'conflict' => DependencyState.conflict,
        'cyclic' => DependencyState.cyclic,
        _ => DependencyState.truncated,
      },
      reason: _emptyToNull(status['reason'] as String?) ??
          _emptyToNull(status['detail'] as String?),
      downloads: DownloadStats.fromJson(_asMap(j['downloads'])),
      depth: _asInt(j['depth']) ?? 0,
      children: _asList(j['children'])
          .whereType<Map<String, dynamic>>()
          .map(DependencyNode.fromJson)
          .toList(),
    );
  }

  /// 深度优先展平，供列表渲染与逐条入场动画使用。
  List<DependencyNode> flatten() {
    final out = <DependencyNode>[];
    void walk(DependencyNode n) {
      out.add(n);
      for (final c in n.children) {
        walk(c);
      }
    }

    walk(this);
    return out;
  }
}

class DependencySummary {
  const DependencySummary({
    required this.total,
    required this.unverified,
    required this.missing,
    required this.conflict,
    required this.cyclic,
    required this.truncated,
    required this.maxDepth,
  });

  final int total;
  final int unverified;
  final int missing;
  final int conflict;
  final int cyclic;
  final int truncated;
  final int maxDepth;

  bool get hasProblems => unverified > 0 || missing > 0 || conflict > 0;

  factory DependencySummary.fromJson(Map<String, dynamic>? j) => DependencySummary(
        total: _asInt(j?['total']) ?? 0,
        unverified: _asInt(j?['unverified']) ?? 0,
        missing: _asInt(j?['missing']) ?? 0,
        conflict: _asInt(j?['conflict']) ?? 0,
        cyclic: _asInt(j?['cyclic']) ?? 0,
        truncated: _asInt(j?['truncated']) ?? 0,
        maxDepth: _asInt(j?['max_depth']) ?? 0,
      );
}

class DependencyTree {
  const DependencyTree({
    required this.root,
    required this.summary,
    required this.warnings,
    required this.truncated,
  });

  final DependencyNode root;
  final DependencySummary summary;

  /// 解析过程中的非致命问题（网络异常、源访问失败等兜底记录）
  final List<String> warnings;
  final bool truncated;

  factory DependencyTree.fromJson(Map<String, dynamic> j) => DependencyTree(
        root: DependencyNode.fromJson(_asMap(j['root']) ?? const {}),
        summary: DependencySummary.fromJson(_asMap(j['summary'])),
        warnings: _asStringList(j['warnings']),
        truncated: j['truncated'] == true,
      );
}

/// 安装计划（阶段3 执行，阶段1 只展示与确认）
class InstallPlan {
  const InstallPlan({
    required this.program,
    required this.args,
    required this.explanation,
  });

  final String program;
  final List<String> args;
  final String explanation;

  String get commandLine => '$program ${args.join(' ')}';

  factory InstallPlan.fromJson(Map<String, dynamic> j) => InstallPlan(
        program: j['program'] as String? ?? '',
        args: _asStringList(j['args']),
        explanation: j['explanation'] as String? ?? '',
      );
}

// ---------------------------------------------------------------------------
// 包源框架信息与错误
// ---------------------------------------------------------------------------

class CoreInfo {
  const CoreInfo({
    required this.app,
    required this.coreVersion,
    required this.platform,
    required this.platformDisplay,
    required this.arch,
    required this.processRunner,
    required this.availabilityTtlSecs,
  });

  final String app;
  final String coreVersion;
  final String platform;
  final String platformDisplay;
  final String arch;

  /// 进程执行器：`cpp-bridge` 表示进程调用确实经过 C++ 桥接层
  final String processRunner;
  final int availabilityTtlSecs;

  factory CoreInfo.fromJson(Map<String, dynamic> j) => CoreInfo(
        app: j['app'] as String? ?? 'UMango',
        coreVersion: j['core_version'] as String? ?? '?',
        platform: j['platform'] as String? ?? '?',
        platformDisplay: j['platform_display'] as String? ?? '?',
        arch: j['arch'] as String? ?? '?',
        processRunner: j['process_runner'] as String? ?? '?',
        availabilityTtlSecs: _asInt(j['availability_ttl_secs']) ?? 0,
      );
}

/// UI 侧对错误的应对建议。与 Rust `RecoveryAdvice` 对应。
enum RecoveryAdvice {
  retry('重试'),
  installCli('安装命令行工具'),
  switchSource('换个包源'),
  showCached('使用缓存数据'),
  checkNetwork('检查网络'),
  hideEntry('隐藏入口'),
  manual('需要手动处理');

  const RecoveryAdvice(this.displayName);
  final String displayName;

  static RecoveryAdvice tryParse(String? raw) => switch (raw) {
        'retry' => RecoveryAdvice.retry,
        'install_cli' => RecoveryAdvice.installCli,
        'switch_source' => RecoveryAdvice.switchSource,
        'show_cached' => RecoveryAdvice.showCached,
        'check_network' => RecoveryAdvice.checkNetwork,
        'hide_entry' => RecoveryAdvice.hideEntry,
        _ => RecoveryAdvice.manual,
      };
}

/// 核心层错误。**带机器可读 code 与兜底建议**，界面据此给出可操作的提示，
/// 而不是把一段原始字符串甩给用户。
class UmangoError implements Exception {
  const UmangoError({
    required this.code,
    required this.message,
    required this.advice,
    required this.retryable,
  });

  final String code;
  final String message;
  final RecoveryAdvice advice;
  final bool retryable;

  factory UmangoError.fromJson(Map<String, dynamic> j) => UmangoError(
        code: j['code'] as String? ?? 'unknown',
        message: j['message'] as String? ?? '未知错误',
        advice: RecoveryAdvice.tryParse(j['advice'] as String?),
        retryable: j['retryable'] == true,
      );

  @override
  String toString() => '[$code] $message（建议：${advice.displayName}）';
}

// ---------------------------------------------------------------------------
// 解析辅助
// ---------------------------------------------------------------------------

Map<String, dynamic>? _asMap(dynamic v) =>
    v is Map<String, dynamic> ? v : (v is Map ? Map<String, dynamic>.from(v) : null);

List<dynamic> _asList(dynamic v) => v is List ? v : const [];

List<String> _asStringList(dynamic v) =>
    _asList(v).whereType<String>().toList(growable: false);

int? _asInt(dynamic v) {
  if (v is int) return v;
  if (v is num) return v.toInt();
  if (v is String) return int.tryParse(v);
  return null;
}

String? _emptyToNull(String? s) => (s == null || s.trim().isEmpty) ? null : s;

String _periodName(String? raw) => switch (raw) {
      'total' => '总计',
      'last_day' => '近 24 小时',
      'last_week' => '近 7 天',
      'last_month' => '近 30 天',
      _ => '未知周期',
    };

/// 千分位格式化，用于下载量/Star 数展示
String formatCount(int? count) {
  if (count == null) return '—';
  final s = count.toString();
  final buf = StringBuffer();
  for (var i = 0; i < s.length; i++) {
    if (i > 0 && (s.length - i) % 3 == 0) buf.write(',');
    buf.write(s[i]);
  }
  return buf.toString();
}

/// 紧凑数字（12.3k / 4.5M），卡片角标用
String formatCountCompact(int? count) {
  if (count == null) return '—';
  if (count >= 1000000000) return '${(count / 1000000000).toStringAsFixed(1)}B';
  if (count >= 1000000) return '${(count / 1000000).toStringAsFixed(1)}M';
  if (count >= 10000) return '${(count / 1000).toStringAsFixed(1)}k';
  return formatCount(count);
}

@visibleForTesting
String debugModelSummary(PackageMeta meta) =>
    '${meta.qualifiedId}@${meta.latestVersion} (${meta.downloads.metricName})';
