/// 纯逻辑集成测试：动画配置系统 + 数据模型契约。
///
/// 这两个部分不依赖界面，但它们是需求里最容易"看起来对了其实错了"的地方：
///   · 动画向量一旦搞反，视觉差异化就不成立；
///   · 前后端字段契约一旦错位，界面会静默显示空白。
/// 所以用真实抓取的核心层 JSON 与断言把它们钉住。
library;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:umango/animation/animation_config.dart';
import 'package:umango/models/umango_models.dart';

import 'helpers.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('动画配置：需求指定的两个向量', () {
    test('列表入场默认为纵向（PCL-CE 原版为横向）', () {
      const config = AnimationConfig();
      expect(config.listEntrance, ListEntranceStyle.verticalStagger);
      expect(config.listEntrance.isVertical, isTrue);

      final begin = config.beginOffset();
      expect(begin.dx, 0, reason: '纵向入场不应有横向位移');
      expect(begin.dy, greaterThan(0), reason: '应从下方进入');
    });

    test('弹窗默认为向右滑出（自左向右滑入），与 PCL-CE 互为镜像', () {
      const umango = AnimationConfig();
      final classic = AnimationPreset.classic.config;

      expect(umango.dialogMotion, DialogMotionStyle.slideRightward);
      expect(umango.dialogBeginOffset().dx, lessThan(0), reason: '起点在左侧，向右移动');

      // 原版向量必须与之相反
      expect(classic.dialogMotion, DialogMotionStyle.slideLeftward);
      expect(classic.dialogBeginOffset().dx, greaterThan(0), reason: '起点在右侧，向左移动');
      expect(
        classic.dialogBeginOffset().dx.sign,
        -umango.dialogBeginOffset().dx.sign,
      );

      // 列表入场的镜像关系：原版横向（有横向位移），UMango 纵向（无横向位移）
      // 注意不能直接比较两者的 dx 符号 —— 纵向风格 dx 恒为 0，符号比较没有意义。
      expect(classic.listEntrance, ListEntranceStyle.horizontalStagger);
      expect(classic.beginOffset().dx, lessThan(0), reason: '原版自左进入');
      expect(umango.beginOffset().dx, 0, reason: '纵向入场没有横向位移');
      expect(umango.beginOffset().dy, greaterThan(0), reason: '自下而上进入');
    });

    test('逐条延迟封顶在 14 项，长列表不会被延迟拖垮', () {
      const config = AnimationConfig(staggerStep: Duration(milliseconds: 10));
      expect(config.delayForIndex(0), Duration.zero);
      expect(config.delayForIndex(5), const Duration(milliseconds: 50));
      final atCap = config.delayForIndex(14);
      expect(config.delayForIndex(400), atCap, reason: '超出后不再累加');
      expect(config.delayForIndex(400).inMilliseconds, lessThan(1000));
    });

    test('四个预设都能被自身识别，且开关语义正确', () {
      for (final preset in AnimationPreset.values) {
        expect(preset.config.matchingPreset, preset);
      }
      final off = AnimationPreset.off.config;
      expect(off.enabled, isFalse);
      expect(off.listEntrance, ListEntranceStyle.none);
      expect(off.dialogMotion, DialogMotionStyle.none);
    });

    test('copyWith 只改指定字段', () {
      const base = AnimationConfig();
      final changed = base.copyWith(listEntrance: ListEntranceStyle.fadeOnly);
      expect(changed.listEntrance, ListEntranceStyle.fadeOnly);
      expect(changed.dialogMotion, base.dialogMotion);
      expect(changed.offsetExtent, base.offsetExtent);
    });

    testWidgets('AnimationScope 能被读取与回写', (tester) async {
      var captured = const AnimationConfig();
      late AnimationConfig readBack;
      await tester.pumpWidget(AnimationScope(
        config: const AnimationConfig(listEntrance: ListEntranceStyle.fadeOnly),
        onChanged: (c) => captured = c,
        child: Builder(builder: (context) {
          readBack = AnimationScope.of(context);
          return const SizedBox();
        }),
      ));
      expect(readBack.listEntrance, ListEntranceStyle.fadeOnly);
      AnimationScope.update(
        tester.element(find.byType(SizedBox)),
        readBack.copyWith(dialogMotion: DialogMotionStyle.scaleUp),
      );
      expect(captured.dialogMotion, DialogMotionStyle.scaleUp);
    });
  });

  group('核心层契约：core.info', () {
    test('进程执行器必须是 cpp-bridge（三层真接通）', () async {
      final info = CoreInfo.fromJson(await fixtureData('core_info'));
      expect(info.app, 'UMango');
      expect(info.processRunner, 'cpp-bridge');
      expect(info.availabilityTtlSecs, 24 * 3600);
      expect(info.platform, anyOf('macos', 'windows', 'linux'));
    });
  });

  group('核心层契约：sources.list（平台门控）', () {
    late List<SourceView> sources;

    setUpAll(() async {
      final raw = await fixtureData<List>('sources_list');
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

    test('当前平台不支持的包源被门控且带未验证标注', () {
      final blocked = sources.where((s) => !s.supportedHere).toList();
      expect(blocked, isNotEmpty, reason: '任何平台上都应有不受支持的包源');
      for (final s in blocked) {
        expect(s.availability.state, AvailabilityState.unsupportedPlatform);
        expect(s.isSelectable, isFalse, reason: 'UI 必须隐藏 ${s.kind.id}');
      }
      // winget/scoop 的解析逻辑未在真机验证，必须显式标注
      final winget = sources.firstWhere((s) => s.kind == PackageSourceKind.winget);
      final scoop = sources.firstWhere((s) => s.kind == PackageSourceKind.scoop);
      expect(winget.verificationNote, isNotNull);
      expect(scoop.verificationNote, isNotNull);
    });

    test('npm 有官方下载量能力，pnpm 没有（走 Stars 兜底）', () {
      expect(
        sources.firstWhere((s) => s.kind == PackageSourceKind.npm)
            .capabilities.officialDownloads,
        isTrue,
      );
      expect(
        sources.firstWhere((s) => s.kind == PackageSourceKind.pnpm)
            .capabilities.officialDownloads,
        isFalse,
      );
    });

    test('git 声明不支持依赖解析（仓库没有统一元数据）', () {
      final git = sources.firstWhere((s) => s.kind == PackageSourceKind.git);
      expect(git.capabilities.resolveDeps, isFalse);
      expect(git.capabilities.search, isTrue);
    });
  });

  group('核心层契约：版本与依赖树', () {
    test('版本列表解析出通道且最新在前', () async {
      final raw = await fixtureData<List>('versions_npm');
      final versions = raw
          .map((e) => PackageVersion.fromJson((e as Map).cast<String, dynamic>()))
          .toList();
      expect(versions, isNotEmpty);
      expect(versions.any((v) => v.channel == ReleaseChannel.stable), isTrue);
      expect(versions.first.version.startsWith('7'), isTrue,
          reason: 'is-number 最新主版本应为 7，实际 ${versions.first.version}');
    });

    test('依赖树完整解析、无降级警告', () async {
      final tree = DependencyTree.fromJson(await fixtureData('dependencies_npm'));
      expect(tree.root.name, 'is-odd');
      expect(tree.root.state, DependencyState.resolved);
      expect(tree.root.children, isNotEmpty);
      expect(tree.warnings, isEmpty);
      expect(tree.summary.total, 2);
      expect(tree.summary.hasProblems, isFalse);

      final child = tree.root.children.first;
      expect(child.name, 'is-number');
      expect(child.requirement, '^6.0.0');
      expect(child.resolved, '6.0.0', reason: '范围应解析出具体版本');
      expect(child.depth, 1);
    });

    test('flatten 深度优先展开，供列表逐条入场', () async {
      final tree = DependencyTree.fromJson(await fixtureData('dependencies_npm'));
      final flat = tree.root.flatten();
      expect(flat.length, 2);
      expect(flat[0].name, 'is-odd');
      expect(flat[1].name, 'is-number');
    });
  });

  group('核心层契约：下载量三段语义', () {
    test('官方下载量：不是兜底，指标名为"下载量"', () async {
      final stats = DownloadStats.fromJson(await fixtureData('downloads_official'));
      expect(stats, isA<OfficialDownloadStats>());
      expect(stats.isFallback, isFalse);
      expect(stats.metricName, '下载量');
      expect(stats.count, greaterThan(1000));
      expect((stats as OfficialDownloadStats).via, 'api.npmjs.org');
    });

    test('GitHub Stars 兜底被明确标注，不冒充下载量', () {
      final stats = DownloadStats.fromJson({
        'kind': 'github_stars',
        'count': 284,
        'repo': 'jonschlinkert/is-number',
      });
      expect(stats.isFallback, isTrue);
      expect(stats.metricName, 'GitHub Stars');
      expect(stats.sourceLabel, contains('jonschlinkert/is-number'));
    });

    test('拿不到数据是"暂无数据"而非 0', () {
      final stats = DownloadStats.fromJson({
        'kind': 'unknown',
        'reason': 'pnpm 没有独立的下载量统计接口',
      });
      expect(stats.count, isNull);
      expect(stats.metricName, '暂无数据');
    });
  });

  group('错误信封与容错', () {
    test('ok=false 翻译成带 code 与建议的错误', () async {
      final env = await loadEnvelope('error_envelope');
      expect(env['ok'], isFalse);
      final error =
          UmangoError.fromJson((env['error'] as Map).cast<String, dynamic>());
      expect(error.code, 'package_not_found');
      expect(RecoveryAdvice.values, contains(error.advice));
    });

    test('字段全缺失也不会崩，降级为安全默认值', () {
      final meta = PackageMeta.fromJson(const {});
      expect(meta.name, isNotEmpty);
      expect(meta.downloads.count, isNull);

      final node = DependencyNode.fromJson(const {});
      expect(node.state, DependencyState.truncated);

      final view = SourceView.fromJson(const {});
      expect(view.supportedHere, isFalse);
    });

    test('数字格式化', () {
      expect(formatCountCompact(125809326), '125.8M');
      expect(formatCountCompact(284), '284');
      expect(formatCount(null), '—');
      expect(formatCount(1234567), '1,234,567');
    });
  });
}
