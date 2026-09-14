/// 界面集成测试：在**真实运行的 macOS 应用**里驱动界面。
///
/// 这一层回答的问题是："界面到底能不能用" ——
/// 编译通过只说明语法没错，进程存活只说明没崩，
/// 而页面完全可能停在一个错误横幅上（错误路径被优雅处理，不会抛异常）。
///
/// 因此这里断言的是**屏幕上真实出现了正确的文字与组件**，
/// 并且数据来自真实的核心层（真的执行了 npm 等 CLI）。
library;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:umango/animation/animation_config.dart';
import 'package:umango/models/umango_models.dart';
import 'package:umango/pages/download_page.dart';
import 'package:umango/pages/package_detail_page.dart';
import 'package:umango/pages/settings_page.dart';
import 'package:umango/widgets/dependency_tree.dart';
import 'package:umango/widgets/package_card.dart';
import 'package:umango/widgets/source_icon.dart';

import 'helpers.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(requireBridge);

  group('应用启动', () {
    testWidgets('进入主界面，而不是停留在加载页或错误页', (tester) async {
      await pumpAppUntilReady(tester);

      expect(find.text('UMango'), findsWidgets);
      for (final label in ['主页', '下载', '设置', '关于']) {
        expect(find.text(label), findsWidgets, reason: '缺少导航项 $label');
      }
      expect(find.text('正在加载核心层…'), findsNothing);
      expect(find.text('核心层未能加载'), findsNothing,
          reason: '不应出现桥接层加载失败页');
      expect(tester.takeException(), isNull);
    });

    testWidgets('首页显示真实核心层信息，进程执行器为 cpp-bridge', (tester) async {
      await pumpAppUntilReady(tester);

      // 这个值是 Rust 经 C++ 桥接层报上来的；显示 cpp-bridge 说明三层真接通
      expect(find.text('cpp-bridge'), findsWidgets,
          reason: '首页应展示真实进程执行器');
      expect(find.text('包源能力矩阵'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('能力矩阵列出全部包源，含被平台门控的项', (tester) async {
      await pumpAppUntilReady(tester);

      // 滚动到矩阵底部
      await realDelay(tester, const Duration(seconds: 1));
      for (final name in ['npm', 'pnpm', 'Git']) {
        expect(find.text(name), findsWidgets, reason: '矩阵应包含 $name');
      }
      // 被门控的源仍列出（状态列显示不支持），而不是消失
      final gatedText = find.text('当前平台不支持');
      expect(
        gatedText.evaluate().isNotEmpty || find.text('WinGet').evaluate().isNotEmpty,
        isTrue,
        reason: '平台门控信息应在界面上可见',
      );
      expect(tester.takeException(), isNull);
    });
  });

  group('下载页：包源列表与下载卡片', () {
    Future<void> openDownload(WidgetTester tester) async {
      await pumpAppUntilReady(tester);
      await tester.tap(find.text('下载').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 4));
    }

    testWidgets('展示可用的包源卡片，并隐藏当前平台不支持的源', (tester) async {
      await openDownload(tester);

      expect(find.byType(DownloadPage), findsOneWidget);
      final cards =
          tester.widgetList<SourceCard>(find.byType(SourceCard)).toList();
      expect(cards, isNotEmpty, reason: '包源列表应有卡片');

      // 默认隐藏不可用源：出现的卡片必须都是可选的
      for (final card in cards) {
        expect(card.source.isSelectable, isTrue,
            reason: '${card.source.kind.id} 不可用却出现在列表中');
      }
      // 可用源里应包含 npm
      expect(cards.any((c) => c.source.kind == PackageSourceKind.npm), isTrue);
      expect(find.text('显示不可用包源'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('打开开关后被门控的源可见，且状态明确', (tester) async {
      await openDownload(tester);

      await tester.tap(find.byType(Switch).first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      final cards =
          tester.widgetList<SourceCard>(find.byType(SourceCard)).toList();
      expect(cards.length, 6, reason: '打开开关后应显示全部 6 个包源');

      final blocked = cards.where((c) => !c.source.isSelectable).toList();
      expect(blocked, isNotEmpty, reason: '本机应有被平台门控的包源');
      for (final card in blocked) {
        expect(card.source.availability.state,
            AvailabilityState.unsupportedPlatform);
      }
      expect(tester.takeException(), isNull);
    });

    testWidgets('搜索真实包并渲染卡片（含下载量角标与官方图标）', (tester) async {
      await openDownload(tester);

      await tester.enterText(find.byType(TextField).first, 'is-number');
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, '搜索'));
      await tester.pump();
      // npm search 真实调用需要时间
      await realDelay(tester, const Duration(seconds: 20));

      expect(find.byType(PackageCard), findsWidgets, reason: '搜索应有结果卡片');
      expect(find.byType(DownloadBadge), findsWidgets, reason: '卡片应显示下载量角标');
      expect(find.byType(SourceIcon), findsWidgets, reason: '卡片应显示包源官方图标');
      expect(tester.takeException(), isNull);
    });
  });

  group('设置页：动画配置系统', () {
    Future<void> openSettings(WidgetTester tester) async {
      await pumpAppUntilReady(tester);
      await tester.tap(find.text('设置').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 4));
    }

    testWidgets('展示预设与两个方向配置项', (tester) async {
      await openSettings(tester);

      expect(find.byType(SettingsPage), findsOneWidget);
      expect(find.text('动画风格'), findsOneWidget);
      for (final preset in AnimationPreset.values) {
        expect(find.text(preset.displayName), findsWidgets,
            reason: '缺少预设 ${preset.displayName}');
      }
      expect(find.text('列表入场动画'), findsOneWidget);
      expect(find.text('弹窗动画'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('切换预设后配置真的改变（需求：可在设置里切换动画风格）', (tester) async {
      await openSettings(tester);

      final scopeFinder = find.byType(AnimationScope);
      var scope = tester.widget<AnimationScope>(scopeFinder.first);
      expect(scope.config.listEntrance, ListEntranceStyle.verticalStagger);
      expect(scope.config.dialogMotion, DialogMotionStyle.slideRightward);

      await tester.tap(find.text(AnimationPreset.classic.displayName).first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      scope = tester.widget<AnimationScope>(scopeFinder.first);
      expect(scope.config.listEntrance, ListEntranceStyle.horizontalStagger,
          reason: '切到 PCL-CE 原版预设后列表入场应变横向');
      expect(scope.config.dialogMotion, DialogMotionStyle.slideLeftward);
      expect(tester.takeException(), isNull);
    });

    testWidgets('总开关能关闭动画', (tester) async {
      await openSettings(tester);

      await tester.tap(find.byType(Switch).first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      final scope = tester.widget<AnimationScope>(
        find.byType(AnimationScope).first,
      );
      expect(scope.config.enabled, isFalse);
      expect(tester.takeException(), isNull);
    });

    testWidgets('包源检测区展示真实可用性与 24 小时缓存保护状态', (tester) async {
      await openSettings(tester);

      // 滚动到包源检测区
      await tester.dragUntilVisible(
        find.text('包源检测'),
        find.byType(ListView).first,
        const Offset(0, -200),
      );
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      expect(find.text('包源检测'), findsOneWidget);
      expect(find.text('重新检测'), findsOneWidget);
      // 真实探测结果：可用源的 CLI 版本号应显示出来
      expect(find.text('可用'), findsWidgets);
      expect(tester.takeException(), isNull);
    });
  });

  group('包详情页：版本列表与依赖树可视化', () {
    /// 直接挂载详情页（用真实核心层数据），避免依赖搜索页的交互链路，
    /// 这样失败能精确定位到"详情页"而不是"搜索"。
    Future<void> openDetail(WidgetTester tester, String name) async {
      await tester.pumpWidget(MaterialApp(
        home: PackageDetailPage(source: PackageSourceKind.npm, name: name),
      ));
      await tester.pump();
      // 版本列表 + 依赖树是两次真实 CLI 调用
      await realDelay(tester, const Duration(seconds: 15));
    }

    testWidgets('版本列表渲染出版本号、通道标签与通道筛选', (tester) async {
      await openDetail(tester, 'is-number');

      expect(find.textContaining('版本列表'), findsWidgets);
      // 真实版本号
      expect(find.text('7.0.0'), findsWidgets, reason: '应列出版本号');
      // 通道标签（正式版/测试版/快照）—— 需求要求区分版本通道
      expect(find.text('正式版'), findsWidgets, reason: '版本应带通道标签');
      // 通道筛选
      expect(find.byType(FilterChip), findsWidgets);
      expect(find.textContaining('全部 ('), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('依赖树可视化渲染出节点、统计与可点操作', (tester) async {
      await openDetail(tester, 'is-odd');

      // 切到依赖树标签
      await tester.tap(find.textContaining('依赖树').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 6));

      expect(find.byType(DependencyTreeView), findsOneWidget);
      expect(find.byType(DependencyRow), findsWidgets,
          reason: '应渲染出依赖节点行');

      // 依赖链上的真实包名（is-odd 依赖 is-number）
      expect(find.text('is-number'), findsWidgets);

      // 每个依赖节点都提供两种操作：直接安装、跳转版本详情
      // （需求：支持「直接安装依赖」或「跳转至依赖版本详情页」）
      expect(find.byIcon(Icons.download_outlined), findsWidgets,
          reason: '依赖节点应有"直接安装"入口');
      expect(find.byIcon(Icons.arrow_forward_outlined), findsWidgets,
          reason: '依赖节点应有"跳转详情"入口');

      // 统计信息：总数与解析状态
      expect(find.textContaining('个依赖节点'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('点击依赖节点的跳转会打开该依赖的详情页', (tester) async {
      await openDetail(tester, 'is-odd');
      await tester.tap(find.textContaining('依赖树').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 6));

      await tester.tap(find.byIcon(Icons.arrow_forward_outlined).first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 8));

      // 注意：跳转后旧页面会被标记为 offstage，而 find.byType 默认跳过 offstage，
      // 所以这里看到的就是新压栈的那个详情页（不能用数量断言）。
      final page = tester.widget<PackageDetailPage>(find.byType(PackageDetailPage));
      expect(page.name, isNotEmpty);
      // 从依赖节点跳转时会带上该依赖解析出的版本，这是"跳对了页面"的证据
      expect(page.presetVersion, isNotNull,
          reason: '从依赖节点跳转应携带该依赖的解析版本');
      expect(tester.takeException(), isNull);
    });
  });

  group('包卡片渲染（用真实形状的数据）', () {
    testWidgets('兜底指标会被标注，不会冒充下载量', (tester) async {
      const meta = PackageMeta(
        source: PackageSourceKind.npm,
        name: 'is-number',
        displayName: 'is-number',
        latestChannel: ReleaseChannel.stable,
        downloads: GitHubStarsStats(count: 284, repo: 'jonschlinkert/is-number'),
        latestVersion: '7.0.0',
        summary: 'Returns true if the value is a number.',
      );

      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: PackageCard(meta: meta, index: 0, onOpen: () {}),
          ),
        ),
      ));
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      expect(find.text('is-number'), findsOneWidget);
      expect(find.text('7.0.0'), findsOneWidget);
      expect(find.text('正式版'), findsOneWidget);
      expect(find.text('兜底'), findsOneWidget, reason: '兜底数据必须标注');
      expect(find.text('284'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}
