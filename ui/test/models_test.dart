/// 数据模型解析测试。
///
/// **fixtures 里的 JSON 是真实抓取的**（用 Python ctypes 调用 C ABI 得到的核心层
/// 输出），不是手写样例。因此这些测试同时验证：
///   1. Rust 侧序列化的字段名与 Dart 侧解析的字段名一致；
///   2. 枚举字符串（channel / state / kind / advice）两端一致；
///   3. 兜底语义（官方下载量 vs GitHub Stars vs 暂无数据）被正确区分。
///
/// 契约一旦被改坏，这里会直接失败。
library;

import 'dart:convert';

import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_test/flutter_test.dart';
import 'package:umango/models/umango_models.dart';

/// 读取真实抓取的核心层输出信封。
Future<Map<String, dynamic>> _envelope(String name) async {
  final raw = await rootBundle.loadString('assets/fixtures/$name.json');
  return jsonDecode(raw) as Map<String, dynamic>;
}

/// 读取信封里的 data 字段（保持原始 JSON 形状）。
Future<dynamic> _data(String name) async {
  final env = await _envelope(name);
  expect(env['ok'], isTrue, reason: 'fixture $name 应为成功响应');
  return env['data'];
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('core.info', () {
    test('解析出应用与版本信息', () async {
      final info = CoreInfo.fromJson(
        (await _data('core_info') as Map).cast<String, dynamic>(),
      );
      expect(info.app, 'UMango');
      expect(info.coreVersion, isNotEmpty);
      expect(info.platform, anyOf('macos', 'windows', 'linux'));
    });

    test('进程执行器是 cpp-bridge（三层真的接通了）', () async {
      final info = CoreInfo.fromJson(
        (await _data('core_info') as Map).cast<String, dynamic>(),
      );
      // 若这里变成 native，说明 C++ 桥接层没参与进程调用
      expect(info.processRunner, 'cpp-bridge');
    });

    test('可用性缓存 TTL 是 24 小时', () async {
      final info = CoreInfo.fromJson(
        (await _data('core_info') as Map).cast<String, dynamic>(),
      );
      expect(info.availabilityTtlSecs, 24 * 3600);
    });
  });

  group('sources.list（平台门控与可用性）', () {
    late List<SourceView> sources;

    setUpAll(() async {
      final raw = await _data('sources_list') as List;
      sources = raw
          .map((e) => SourceView.fromJson((e as Map).cast<String, dynamic>()))
          .toList();
    });

    test('返回全部 6 个包源', () {
      expect(sources.length, 6);
      expect(
        sources.map((s) => s.kind).toSet(),
        PackageSourceKind.values.toSet(),
      );
    });

    test('当前平台上不支持的包源被门控且带未验证标注', () {
      final blocked = sources.where((s) => !s.supportedHere).toList();
      expect(blocked, isNotEmpty, reason: '任何平台上都应有不受支持的包源');
      for (final s in blocked) {
        expect(s.availability.state, AvailabilityState.unsupportedPlatform);
        expect(s.isSelectable, isFalse, reason: 'UI 必须隐藏 ${s.kind.id}');
      }
      final winget = sources.firstWhere((s) => s.kind == PackageSourceKind.winget);
      final scoop = sources.firstWhere((s) => s.kind == PackageSourceKind.scoop);
      expect(winget.verificationNote, isNotNull);
      expect(scoop.verificationNote, isNotNull);
    });

    test('npm 有官方下载量能力，pnpm 没有（走 Stars 兜底）', () {
      expect(
        sources
            .firstWhere((s) => s.kind == PackageSourceKind.npm)
            .capabilities
            .officialDownloads,
        isTrue,
      );
      expect(
        sources
            .firstWhere((s) => s.kind == PackageSourceKind.pnpm)
            .capabilities
            .officialDownloads,
        isFalse,
      );
    });

    test('git 声明不支持依赖解析（仓库没有统一元数据）', () {
      final git = sources.firstWhere((s) => s.kind == PackageSourceKind.git);
      expect(git.capabilities.resolveDeps, isFalse);
      expect(git.capabilities.search, isTrue);
    });
  });

  group('packages.versions', () {
    test('解析出版本列表，通道为枚举且最新在前', () async {
      final raw = await _data('versions_npm') as List;
      final versions = raw
          .map((e) => PackageVersion.fromJson((e as Map).cast<String, dynamic>()))
          .toList();
      expect(versions, isNotEmpty);
      expect(versions.any((v) => v.channel == ReleaseChannel.stable), isTrue);
      expect(versions.first.version.startsWith('7'), isTrue,
          reason: 'is-number 最新主版本应为 7，实际 ${versions.first.version}');
    });
  });

  group('packages.dependencies（依赖树）', () {
    Future<DependencyTree> tree() async => DependencyTree.fromJson(
          (await _data('dependencies_npm') as Map).cast<String, dynamic>(),
        );

    test('根节点与子节点都解析成功，且无降级警告', () async {
      final t = await tree();
      expect(t.root.name, 'is-odd');
      expect(t.root.resolved, '3.0.1');
      expect(t.root.state, DependencyState.resolved);
      expect(t.root.children, isNotEmpty);
      expect(t.warnings, isEmpty);
      expect(t.summary.total, 2);
      expect(t.summary.hasProblems, isFalse);
    });

    test('依赖的版本范围被解析成具体版本', () async {
      final child = (await tree()).root.children.first;
      expect(child.name, 'is-number');
      expect(child.requirement, '^6.0.0');
      expect(child.resolved, '6.0.0', reason: '应解析出具体版本而不是留着范围');
      expect(child.depth, 1);
    });

    test('flatten 按深度优先展开，供列表逐条入场使用', () async {
      final flat = (await tree()).root.flatten();
      expect(flat.length, 2);
      expect(flat[0].name, 'is-odd');
      expect(flat[1].name, 'is-number');
    });
  });

  group('下载量三段语义（本需求的核心规则）', () {
    test('官方下载量：不是兜底，指标名是"下载量"', () async {
      final stats = DownloadStats.fromJson(
        (await _data('downloads_official') as Map).cast<String, dynamic>(),
      );
      expect(stats, isA<OfficialDownloadStats>());
      expect(stats.metricName, '下载量');
      expect(stats.isFallback, isFalse);
      expect(stats.count, greaterThan(1000));
      expect((stats as OfficialDownloadStats).via, 'api.npmjs.org');
      // 注意：Dart 模型会把核心层给的 period 枚举（last_week）本地化成展示文案，
      // 所以这里断言的是本地化后的结果，而不是原始 JSON 值。
      expect(stats.period, '近 7 天');
    });

    test('GitHub Stars 兜底被明确标注，不会冒充下载量', () {
      final stats = DownloadStats.fromJson({
        'kind': 'github_stars',
        'count': 284,
        'repo': 'jonschlinkert/is-number',
      });
      expect(stats, isA<GitHubStarsStats>());
      expect(stats.isFallback, isTrue);
      expect(stats.metricName, 'GitHub Stars', reason: 'Star 数不能显示成"下载量"');
      expect(stats.sourceLabel, contains('jonschlinkert/is-number'));
    });

    test('拿不到数据时是"暂无数据"而不是 0', () {
      final stats = DownloadStats.fromJson({
        'kind': 'unknown',
        'reason': 'pnpm 没有独立的下载量统计接口',
      });
      expect(stats, isA<UnknownDownloadStats>());
      expect(stats.metricName, '暂无数据');
      expect(stats.count, isNull, reason: '拿不到与 0 必须区分开');
      expect(stats.sourceLabel, contains('pnpm'));
    });

    test('空 JSON 也不会崩，降级为未知', () {
      final stats = DownloadStats.fromJson(null);
      expect(stats.count, isNull);
      expect(stats.metricName, '暂无数据');
    });
  });

  group('错误信封', () {
    test('ok=false 被翻译成带 code 与建议的 UmangoError', () async {
      final env = await _envelope('error_envelope');
      expect(env['ok'], isFalse);
      final error =
          UmangoError.fromJson((env['error'] as Map).cast<String, dynamic>());
      expect(error.code, 'package_not_found');
      expect(error.message, isNotEmpty);
      expect(RecoveryAdvice.values, contains(error.advice));
      expect(error.toString(), contains(error.code));
    });

    test('未知 advice 字符串退化为 manual 而不是抛异常', () {
      final error = UmangoError.fromJson({
        'code': 'x',
        'message': 'y',
        'advice': 'some_future_advice',
        'retryable': true,
      });
      expect(error.advice, RecoveryAdvice.manual);
    });
  });

  group('容错：字段缺失不应让界面崩掉', () {
    test('空对象解析出安全默认值', () {
      final meta = PackageMeta.fromJson(const {});
      expect(meta.name, isNotEmpty);
      expect(meta.source, PackageSourceKind.npm);
      expect(meta.downloads.count, isNull);

      final node = DependencyNode.fromJson(const {});
      expect(node.state, DependencyState.truncated);

      final view = SourceView.fromJson(const {});
      expect(view.supportedHere, isFalse);
      expect(view.availability.state, AvailabilityState.unreachable);
    });

    test('数字格式化：紧凑显示与千分位', () {
      expect(formatCountCompact(125809326), '125.8M');
      expect(formatCountCompact(284), '284');
      expect(formatCountCompact(15000), '15.0k');
      expect(formatCount(null), '—');
      expect(formatCount(1234567), '1,234,567');
    });
  });
}
