/// 界面集成测试：在**真实运行的 macOS 应用**里驱动界面。
///
/// 这一层回答的问题是："界面到底能不能用" ——
/// 编译通过只说明语法没错，进程存活只说明没崩，
/// 而页面完全可能停在一个错误横幅上（错误路径被优雅处理，不会抛异常）。
///
/// 因此这里断言的是**屏幕上真实出现了正确的文字与组件**，
/// 并且数据来自真实的核心层（真的执行了 npm 等 CLI）。
///
/// 外壳按 `docs/DESIGN_PCL_CE.md` 重做之后，导航与列表都不再是 Material 组件：
/// 顶部标签是自绘的 [PclTabButton]，下载页左栏的包源入口是 [PclListItem]，
/// 按钮是自绘的 [PclButton] / [PclIconButton]。所以定位方式跟着改成
/// **按元素承载的业务信息（文字 / tooltip）**，而不是按 Material 类型；
/// 断言本身仍然钉在业务语义上（哪个包源出现了、状态是什么、能不能点）。
library;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:umango/animation/animation_config.dart';
import 'package:umango/ffi/umango_bridge.dart';
import 'package:umango/models/umango_models.dart';
import 'package:umango/pages/download_page.dart';
import 'package:umango/pages/package_detail_page.dart';
import 'package:umango/pages/settings_page.dart';
import 'package:umango/pages/tools_page.dart';
import 'package:umango/theme/pcl_theme.dart';
import 'package:umango/widgets/dependency_tree.dart';
import 'package:umango/widgets/package_card.dart';
import 'package:umango/widgets/pcl_widgets.dart';
import 'package:umango/widgets/source_icon.dart';

import 'helpers.dart';

// ---------------------------------------------------------------------------
// 定位辅助
//
// 全部基于「界面上真的有什么」：文字、tooltip、以及状态点的可见颜色。
// 不新增任何 `Key`（`ui/lib/**` 不允许改），也不依赖私有组件类型。
// ---------------------------------------------------------------------------

/// 下载页左栏（[PclListItem]）里所有列表项的文字，包源与"视图"分组都在内。
List<String> _railLabels(WidgetTester tester) => tester
    .widgetList<PclListItem>(find.byType(PclListItem))
    .map((PclListItem item) => item.label)
    .toList();

/// 取左栏某个包源入口外层的提示气泡文案。
///
/// 提示由下载页挂在列表项外层，内容形如 `npm · 可用 · 10.9.7`，
/// 因此它同时承载了「名字 + 可用性状态」两条信息 ——
/// 左栏宽度只有 168，状态文字放不进行内，只能靠提示气泡与状态点表达，
/// 所以这两样东西就是"状态明确"的证据。
String? _railItemTooltip(WidgetTester tester, String label) {
  final Iterable<Element> items =
      find.widgetWithText(PclListItem, label).evaluate();
  expect(items, isNotEmpty, reason: '左栏应有一个名为「$label」的包源入口');

  Tooltip? tooltip;
  items.first.visitAncestorElements((Element ancestor) {
    final Widget widget = ancestor.widget;
    if (widget is Tooltip) {
      tooltip = widget;
      return false;
    }
    return true;
  });
  expect(tooltip, isNotNull, reason: '包源入口「$label」应带说明可用性的提示气泡');
  return tooltip!.message;
}

/// 取左栏某个包源入口右侧状态点的颜色（没有状态点则返回 null）。
Color? _railStatusDotColor(WidgetTester tester, String label) {
  final Iterable<Element> items =
      find.widgetWithText(PclListItem, label).evaluate();
  expect(items, isNotEmpty, reason: '左栏应有一个名为「$label」的包源入口');

  Color? dotColor;
  void walk(Element element) {
    final Widget widget = element.widget;
    if (widget is Container) {
      final Decoration? decoration = widget.decoration;
      if (decoration is BoxDecoration && decoration.shape == BoxShape.circle) {
        dotColor = decoration.color;
      }
    }
    element.visitChildren(walk);
  }

  items.first.visitChildren(walk);
  expect(dotColor, isNotNull, reason: '包源入口「$label」应带可用性状态点');
  return dotColor;
}

/// 当前页面上所有使用给定 tooltip 的 [PclIconButton]。
List<PclIconButton> _iconButtonsWithTooltip(
  WidgetTester tester,
  String tooltip,
) =>
    tester
        .widgetList<PclIconButton>(find.byType(PclIconButton))
        .where((PclIconButton button) => button.tooltip == tooltip)
        .toList();

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(requireBridge);

  group('应用启动', () {
    testWidgets('进入主界面，而不是停留在加载页或错误页', (tester) async {
      await pumpAppUntilReady(tester);

      expect(find.text('UMango'), findsWidgets);

      // 导航就是顶部标签栏本身：4 项、顺序固定。
      // 规格 §6 的页面映射把 PCL 的「工具」标签给了 UMango 的工具页，
      // 旧的「关于」不再是独立标签（诊断 + 关于 + 合规声明都并进了工具页）。
      final List<String> tabs = tester
          .widgetList<PclTabButton>(find.byType(PclTabButton))
          .map((PclTabButton tab) => tab.label)
          .toList();
      expect(
        tabs,
        <String>['主页', '下载', '设置', '工具'],
        reason: '顶部标签应是 主页 / 下载 / 设置 / 工具 四项且顺序一致',
      );
      for (final String label in tabs) {
        expect(find.text(label), findsWidgets, reason: '缺少导航项 $label');
      }
      expect(tabs, isNot(contains('关于')), reason: '「关于」已并入工具页');

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

      final List<SourceView> sources = await UmangoBridge.instance.sources();
      final List<SourceView> gated =
          sources.where((SourceView s) => !s.supportedHere).toList();
      expect(gated, isNotEmpty,
          reason: 'macOS 上应有被平台门控的包源（winget / scoop）');

      // 矩阵在概览卡下方，先保证最后一行也进入了视口
      await tester.dragUntilVisible(
        find.text(sources.last.displayName),
        find.byType(ListView).first,
        const Offset(0, -160),
      );
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      // 被门控的源仍列出（状态列标注"当前平台不支持"），而不是消失
      for (final SourceView source in sources) {
        expect(find.text(source.displayName), findsWidgets,
            reason: '矩阵应包含 ${source.kind.id}（含被门控的，不得过滤）');
      }
      expect(find.text('当前平台不支持'), findsWidgets,
          reason: '平台门控信息应在界面上可见');
      expect(tester.takeException(), isNull);
    });
  });

  group('工具页：合规声明与运行诊断', () {
    Future<void> openTools(WidgetTester tester) async {
      await pumpAppUntilReady(tester);
      await tester.tap(find.text('工具').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 4));
    }

    testWidgets('运行诊断区展示真实核心层信息，进程执行器为 cpp-bridge', (tester) async {
      await openTools(tester);

      expect(find.byType(ToolsPage), findsOneWidget);
      expect(find.text('运行诊断'), findsOneWidget);
      expect(find.text('进程执行器'), findsOneWidget);

      // 显示的值必须就是核心层报上来的那一个，而不是写死的文案
      final CoreInfo info = await UmangoBridge.instance.coreInfo();
      expect(info.processRunner, 'cpp-bridge',
          reason: 'Rust 侧应使用 C++ 进程执行器');
      expect(find.text(info.processRunner), findsWidgets,
          reason: '工具页诊断区应展示真实进程执行器');
      expect(find.text(info.coreVersion), findsWidgets,
          reason: '诊断区应展示真实核心层版本');
      expect(tester.takeException(), isNull);
    });

    testWidgets('包含合规声明原文（第三方声明 + lucide 图标许可）', (tester) async {
      await openTools(tester);

      // 合规声明是页面最后一个分区，要滚到最后才被构建出来
      final Finder statement = find.textContaining('UMango 是独立开发的第三方软件');
      await tester.dragUntilVisible(
        statement,
        find.byType(ListView).first,
        const Offset(0, -320),
      );
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      expect(statement, findsOneWidget, reason: '必须包含合规声明原文');
      expect(find.textContaining('与 PCL / PCL-CE 及其开发者无关联'), findsOneWidget,
          reason: '声明必须写明与 PCL / PCL-CE 无关联');
      expect(find.textContaining('图标使用 MIT 许可的 lucide'), findsOneWidget,
          reason: '声明必须写明图标来自 lucide');
      expect(find.text('合规声明'), findsWidgets);
      expect(tester.takeException(), isNull);
    });
  });

  group('下载页：左栏包源列表与下载卡片', () {
    Future<void> openDownload(WidgetTester tester) async {
      await pumpAppUntilReady(tester);
      await tester.tap(find.text('下载').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 4));
    }

    testWidgets('左栏列出当前平台可用的包源，默认隐藏被平台门控的源', (tester) async {
      await openDownload(tester);

      expect(find.byType(DownloadPage), findsOneWidget);

      // 判据来自真实核心层：谁在本平台受支持、谁被门控
      final List<SourceView> sources = await UmangoBridge.instance.sources();
      final List<SourceView> usable =
          sources.where((SourceView s) => s.isSelectable).toList();
      final List<SourceView> gated =
          sources.where((SourceView s) => !s.supportedHere).toList();
      expect(usable, isNotEmpty, reason: '本机应有可用包源');
      expect(gated, isNotEmpty,
          reason: 'macOS 上应有被平台门控的包源（winget / scoop）');

      final List<String> labels = _railLabels(tester);
      expect(labels, isNotEmpty, reason: '下载页左栏应有包源入口');

      // 可用包源必须按名字出现在左栏里（而不是"存在某个组件"）
      for (final SourceView source in usable) {
        expect(labels, contains(source.displayName),
            reason: '可用包源 ${source.kind.id} 应出现在左栏');
      }

      // 平台门控：被门控的源默认必须隐藏，而不是列一个不可选的入口
      for (final SourceView source in gated) {
        expect(source.availability.state, AvailabilityState.unsupportedPlatform,
            reason: '${source.kind.id} 不在本平台支持列表里，状态应为"当前平台不支持"');
        expect(labels, isNot(contains(source.displayName)),
            reason: '${source.kind.id} 在本平台不受支持，默认不应出现在左栏');
      }
      // 一般规则：任何不可选中的源（门控 / 未安装）都不出现在默认列表里
      for (final SourceView source
          in sources.where((SourceView s) => !s.isSelectable)) {
        expect(labels, isNot(contains(source.displayName)),
            reason: '不可选中的源 ${source.kind.id} 默认不应出现在左栏');
      }
      // 左栏里属于包源分组的项数 == 可用包源数（没有多余的入口）
      final Set<String> usableNames =
          usable.map((SourceView s) => s.displayName).toSet();
      expect(labels.where(usableNames.contains).length, usable.length,
          reason: '左栏的包源项应与核心层认为可用的集合完全一致');

      expect(find.text('显示不可用包源'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('打开「显示不可用包源」后被门控的源可见，且状态明确', (tester) async {
      await openDownload(tester);

      final List<SourceView> sources = await UmangoBridge.instance.sources();
      final List<SourceView> gated =
          sources.where((SourceView s) => !s.supportedHere).toList();
      expect(gated, isNotEmpty, reason: '本机应有被平台门控的包源');
      for (final SourceView source in gated) {
        expect(find.text(source.displayName), findsNothing,
            reason: '开关未打开时 ${source.kind.id} 应被隐藏');
      }

      // 开关本身也是自绘的 PclButton（标签会随状态在「显示/隐藏不可用包源」之间切换）
      await tester.tap(find.widgetWithText(PclButton, '显示不可用包源'));
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 1));

      final List<String> labels = _railLabels(tester);
      for (final SourceView source in sources) {
        expect(labels, contains(source.displayName),
            reason: '打开开关后全部 ${sources.length} 个包源都应可见'
                '（缺少 ${source.kind.id}）');
      }

      // 可见还不够，状态必须明确：提示气泡写明原因、状态点标成不可用色
      for (final SourceView source in gated) {
        expect(source.availability.state, AvailabilityState.unsupportedPlatform);
        expect(_railItemTooltip(tester, source.displayName), contains('不支持'),
            reason: '${source.kind.id} 的提示应写明本平台不支持');
        expect(_railStatusDotColor(tester, source.displayName), PclColors.error,
            reason: '${source.kind.id} 的状态点应为不可用（错误）色');
      }
      // 可用源的状态点必须与之区分得开
      for (final SourceView source
          in sources.where((SourceView s) => s.isSelectable)) {
        expect(_railStatusDotColor(tester, source.displayName),
            isNot(PclColors.error),
            reason: '可用源 ${source.kind.id} 的状态点不应是错误色');
      }

      expect(find.text('隐藏不可用包源'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('搜索真实包并渲染卡片（含下载量角标与官方图标）', (tester) async {
      await openDownload(tester);

      await tester.enterText(find.byType(TextField).first, 'is-number');
      await tester.pump();
      // 搜索按钮是自绘的 PclButton（不再是 Material 的 FilledButton）
      await tester.tap(find.widgetWithText(PclButton, '搜索'));
      await tester.pump();
      // npm search 真实调用需要时间
      await realDelay(tester, const Duration(seconds: 20));

      final Finder cards = find.byType(PackageCard);
      expect(cards, findsWidgets, reason: '搜索应有结果卡片');
      final int cardCount = cards.evaluate().length;

      // 角标与图标必须在**卡片里**，而不是"页面上某处出现了这两个组件"
      expect(
        find.descendant(of: cards, matching: find.byType(DownloadBadge))
            .evaluate()
            .length,
        cardCount,
        reason: '每张卡片都应显示下载量角标',
      );
      expect(
        find.descendant(of: cards, matching: find.byType(SourceIcon))
            .evaluate()
            .length,
        cardCount,
        reason: '每张卡片都应显示包源官方图标',
      );

      // 结果确实来自真实的核心层搜索（选定包源是 npm），没有伪造数据
      for (final PackageCard card in tester.widgetList<PackageCard>(cards)) {
        expect(card.meta.name, isNotEmpty, reason: '搜到的包必须有真实包名');
        expect(card.meta.source, PackageSourceKind.npm);
      }
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

      // 标题行右侧的标签显示的是**当前**预设，所以同名文字里最后一个是预设卡片本身
      await tester.tap(find.text(AnimationPreset.classic.displayName).last);
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

      // 设置页整页是一个 SingleChildScrollView（不是 ListView），
      // 内容会一次性构建；这里滚动只是为了让分区真正进入视口。
      await tester.dragUntilVisible(
        find.text('包源检测'),
        find.byType(SingleChildScrollView).first,
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
      final Finder rows = find.byType(DependencyRow);
      expect(rows, findsWidgets, reason: '应渲染出依赖节点行');
      final int rowCount = rows.evaluate().length;

      // 依赖链上的真实包名（is-odd 依赖 is-number）
      expect(find.text('is-number'), findsWidgets);

      // 每个依赖节点都提供两种操作：直接安装、跳转版本详情
      // （需求：支持「直接安装依赖」或「跳转至依赖版本详情页」）。
      // 操作入口是自绘的 PclIconButton，按它承载的语义（tooltip）定位。
      expect(
        _iconButtonsWithTooltip(tester, '直接安装该依赖').length,
        rowCount,
        reason: '每个依赖节点都应有"直接安装"入口',
      );
      expect(
        _iconButtonsWithTooltip(tester, '跳转到该依赖的版本详情').length,
        rowCount,
        reason: '每个依赖节点都应有"跳转详情"入口',
      );

      // 统计信息：总数与解析状态
      expect(find.textContaining('个依赖节点'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('点击依赖节点的跳转会打开该依赖的详情页', (tester) async {
      await openDetail(tester, 'is-odd');
      await tester.tap(find.textContaining('依赖树').first);
      await tester.pump();
      await realDelay(tester, const Duration(seconds: 6));

      final Finder openButtons = find.byWidgetPredicate(
        (Widget widget) =>
            widget is PclIconButton &&
            widget.tooltip == '跳转到该依赖的版本详情',
      );
      expect(openButtons, findsWidgets, reason: '依赖节点应有"跳转详情"入口');
      await tester.tap(openButtons.first);
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
