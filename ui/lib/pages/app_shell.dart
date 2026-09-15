/// 应用外壳：PCL-CE 风格的**顶部标签栏 + 内容区**。
///
/// 布局与数值严格对应 `docs/DESIGN_PCL_CE.md` 第 6 节：
/// ```
/// Material(背景 PclColors.background)
/// └─ Padding(10)                        ← 四周留白（PclMetrics.windowMargin）
///    └─ Container(圆角 6, clip, 白底, 阴影)
///       └─ Column
///          ├─ 标题栏 (高度 48, 纯色 PclColors.titleBar)
///          │   ├─ 左：自绘 Logo + 应用名（不用 PCL 的图片资源，规格 §0.2）
///          │   ├─ 中：4 个 PclTabButton —— 主页 / 下载 / 设置 / 工具
///          │   └─ 右：主题切换 + 3 个窗口按钮 PclIconButton(28)：minus / box-select / x
///          └─ Expanded(...)：页面内容
/// ```
/// **旧版的「左侧竖向导航（应用名 + 主页/下载/设置/关于 一列）」已按规格删除** ——
/// PCL-CE 的导航在顶部标签栏，左栏改由各页面自己按需渲染（下载页的分类栏等）。
///
/// 本文件只负责外壳的视觉与布局，不含任何业务逻辑：页面切换仅是一个 `setState`，
/// 数据加载 / bridge 调用 / 错误处理全部留在各页面内部，行为与改造前一致。
library;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show SystemNavigator;

import '../animation/motion_widgets.dart';
import '../theme/pcl_theme.dart';
import '../widgets/pcl_widgets.dart';
import 'download_page.dart';
import 'home_page.dart';
import 'settings_page.dart';
import 'tools_page.dart';

// ---------------------------------------------------------------------------
// 外壳局部数值（规格 §2 有明确取值的都直接用 PclMetrics，这里只放派生量）
// ---------------------------------------------------------------------------

/// 标题栏左右两端的内边距：取规格 §2「窗口按钮右边距 12」，左侧用同值保持对称。
const double _titleBarPadding = PclMetrics.windowButtonRightMargin;

/// 窗口按钮之间的间隔：规格 §2 的「间距 32」是**中心/起点间距**，
/// 减去 28 的按钮宽度即 4；有了这 4，三个按钮相对右边距 12 的偏移
/// 正好是规格里写的 12 / 44 / 76。
const double _windowButtonGap =
    PclMetrics.windowButtonSpacing - PclMetrics.windowButtonSize;

/// 主题按钮与窗口按钮之间的间隔：12，用留白把「应用功能」与「窗口控制」分成两组，
/// 避免 4 个 28×28 的按钮被看成一排等价控件。
const double _themeButtonGap = 12;

/// Logo 方块尺寸：与窗口按钮同为 28，标题栏里所有方块等宽，视觉基线整齐。
const double _logoBoxSize = 28;

/// Logo 方块与「UMango」文字之间的间距。
const double _logoTextGap = 8;

// ---------------------------------------------------------------------------
// 标签定义
// ---------------------------------------------------------------------------

/// 顶部标签。顺序即标题栏中的显示顺序（规格 §6 页面映射表：主页 / 下载 / 设置 / 工具）。
enum AppSection {
  home('主页'),
  download('下载'),
  settings('设置'),
  tools('工具');

  const AppSection(this.label);

  /// 标签文字（界面文案保持中文）。
  final String label;
}

/// 主题模式的中文名，用于主题按钮的 Tooltip。
String _themeLabel(ThemeMode mode) => switch (mode) {
  ThemeMode.system => '跟随系统',
  ThemeMode.light => '浅色',
  ThemeMode.dark => '深色',
};

/// 主题按钮图标：深色时显示 moon（当前处于深色），其余显示 sun。
///
/// 用「当前状态」而不是「点击后的结果」作为图标语义，避免同一个图标在不同
/// 起始状态下含义相反（跟随系统在浅色下等价于浅色，故与 light 合并）。
String _themeIcon(ThemeMode mode) =>
    mode == ThemeMode.dark ? 'moon' : 'sun';

// ---------------------------------------------------------------------------
// 外壳
// ---------------------------------------------------------------------------

/// 应用外壳。
///
/// API 契约（不得改名/改签名，`main.dart` 依赖它）：
/// `AppShell({required VoidCallback onToggleTheme, required ThemeMode themeMode})`；
/// 4 个页面由本组件内部的 `switch` 决定，外部不感知当前标签。
class AppShell extends StatefulWidget {
  /// 创建外壳。
  const AppShell({
    super.key,
    required this.onToggleTheme,
    required this.themeMode,
  });

  /// 切换主题（跟随系统 → 浅色 → 深色），由 `main.dart` 注入。
  final VoidCallback onToggleTheme;

  /// 当前主题模式。
  final ThemeMode themeMode;

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  /// 当前标签；默认主页。
  AppSection _section = AppSection.home;

  /// 切换标签。相同标签直接返回，避免无意义的重建（也会打断页面内的滚动位置）。
  void _go(AppSection section) {
    if (section == _section) return;
    setState(() => _section = section);
  }

  @override
  Widget build(BuildContext context) {
    return Material(
      // 窗口外圈的留白底色（规格 §6：Material 背景 = #fbfbfb）
      color: PclColors.background,
      child: Padding(
        // 四周留白 10（规格 §2「外层边距」）
        padding: const EdgeInsets.all(PclMetrics.windowMargin),
        child: Container(
          // clipBehavior 保证内容（尤其标题栏的渐变）被 6 圆角裁掉，
          // 否则渐变会以直角溢出圆角，露出方形尖角。
          clipBehavior: Clip.antiAlias,
          decoration: BoxDecoration(
            color: Colors.white,
            borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
            // 轻微阴影：仅用于把白色窗口从 #fbfbfb 背景上「托」起来，
            // 刻意压得很浅 —— 桌面工具型应用不适合 Material 那种厚重 elevation。
            boxShadow: const <BoxShadow>[
              BoxShadow(
                color: PclColors.shadow,
                blurRadius: 12,
                offset: Offset(0, 2),
              ),
            ],
          ),
          child: Column(
            children: <Widget>[
              // 第一行：标题栏（高度 48）
              _TitleBar(
                section: _section,
                onSelect: _go,
                themeMode: widget.themeMode,
                onToggleTheme: widget.onToggleTheme,
              ),
              // 第二行：页面内容，占满剩余高度
              Expanded(child: _buildPage()),
            ],
          ),
        ),
      ),
    );
  }

  /// 顶部标签 → 页面映射（规格 §6 表格）。
  ///
  /// [PageFadeThrough] 保持改造前的切页动效，并遵循全局动画配置
  /// （配置关闭或系统「减少动态效果」时它自己会退化为直接切换）。
  /// [KeyedSubtree] 用标签作 key，切换时整棵子树重建，页面自己的
  /// `initState` 加载逻辑（如包源列表）行为与旧版一致。
  Widget _buildPage() {
    final Widget page = switch (_section) {
      AppSection.home => HomePage(
        // 主页的「去下载」仍按旧约定由外壳切标签实现（保留传参）
        onGoToDownload: () => _go(AppSection.download),
      ),
      AppSection.download => const DownloadPage(),
      AppSection.settings => SettingsPage(
        // 旧版「关于」是独立标签；新规格把「诊断 + 关于 + 合规声明」并入工具页，
        // 因此这里的「关于」入口改为切到工具标签，而不是删掉这个回调
        // （SettingsPage 的构造签名保持不变，避免影响并行改造该页的 agent）。
        onOpenAbout: () => _go(AppSection.tools),
      ),
      AppSection.tools => const ToolsPage(),
    };

    return Scaffold(
      // 透明 Scaffold 只为提供两个宿主能力，视觉上完全不可见：
      //  1. SnackBar 宿主 —— ScaffoldMessenger.showSnackBar 在**没有任何
      //     Scaffold 后代**时会断言失败（Flutter debug 模式），而页面（工具页的
      //     「复制路径」等）会用到它；
      //  2. 页面内的 TextField / Ink 反馈所需的 Material 层（根 Material 虽在，
      //     但水波纹会画到白色容器之下而看不见）。
      // 不能把它换成不透明底色，否则会盖掉规格要求的白底圆角窗口。
      backgroundColor: Colors.transparent,
      body: PageFadeThrough(
        child: KeyedSubtree(key: ValueKey<AppSection>(_section), child: page),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// 标题栏
// ---------------------------------------------------------------------------

/// 顶部标题栏：左侧自绘 Logo、中间 4 个标签、右侧主题与窗口按钮。
///
/// 背景是 [PclColors.titleBar]（纯色强调蓝）。
///
/// 规格初稿曾写"横向渐变"，但用 PCL-CE 真实运行录屏逐像素采样后确认标题栏是**纯色**：
/// 横向 11 点采样中 9 点颜色完全一致。故这里改为纯色，避免凭想象做渐变。
/// 因此栏内所有前景（Logo 图形、标签文字、图标按钮）都用白色系，
/// 通过 [IconTheme] 把白色透传给 [PclIconButton]（该组件的字形颜色取
/// `IconTheme.of(context).color`）。
class _TitleBar extends StatelessWidget {
  const _TitleBar({
    required this.section,
    required this.onSelect,
    required this.themeMode,
    required this.onToggleTheme,
  });

  /// 当前选中的标签。
  final AppSection section;

  /// 点击标签的回调。
  final ValueChanged<AppSection> onSelect;

  /// 当前主题模式（决定图标与提示文字）。
  final ThemeMode themeMode;

  /// 切换主题。
  final VoidCallback onToggleTheme;

  @override
  Widget build(BuildContext context) {
    return Container(
      height: PclMetrics.titleBarHeight,
      decoration: const BoxDecoration(color: PclColors.titleBar),
      child: Row(
        children: <Widget>[
          const Padding(
            padding: EdgeInsets.only(left: _titleBarPadding),
            child: _AppLogo(),
          ),
          // 中间标签区：占满 Logo 与右侧按钮之间的空间并居中。
          // 用 Expanded + Center（而不是把标签左右各配一个 Spacer）是为了在窗口
          // 变窄时标签区先被压缩、绝不与两侧内容重叠 —— 窗口最小宽度 810 下
          // 仍有充足余量。
          Expanded(
            child: Center(
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  for (final AppSection item in AppSection.values)
                    PclTabButton(
                      label: item.label,
                      selected: item == section,
                      onTap: () => onSelect(item),
                    ),
                ],
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.only(right: PclMetrics.windowButtonRightMargin),
            child: IconTheme(
              // 蓝色渐变上的前景统一为白色（PclIconButton 的字形色来源）
              data: const IconThemeData(color: Colors.white),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: <Widget>[
                  // 主题切换：PCL-CE 本身没有这个按钮，放在窗口按钮左侧并留出
                  // 12 的间隔，与右边的窗口控制分组区分开。
                  PclIconButton(
                    icon: _themeIcon(themeMode),
                    size: PclMetrics.windowButtonSize,
                    tooltip: '切换主题（当前：${_themeLabel(themeMode)}）',
                    onPressed: onToggleTheme,
                  ),
                  const SizedBox(width: _themeButtonGap),
                  /*
                   * 三个窗口按钮只做视觉还原（规格 §6）：macOS 有原生红绿灯按钮，
                   * 这里不可能也不应该接管窗口管理。
                   *
                   * 为什么 minus / box-select 传 onPressed: null 而不是空回调：
                   * PclIconButton 对 null 会渲染成 0.45 不透明度的禁用态（并保留
                   * Tooltip），这正是事实 —— 这两个按钮在本平台没有功能。
                   * 若给一个空回调，按钮会 hover 高亮却什么都不做，属于「假装可点」，
                   * 比诚实禁用更糟。
                   */
                  const PclIconButton(
                    icon: 'minus',
                    size: PclMetrics.windowButtonSize,
                    tooltip: '最小化：由系统窗口按钮接管',
                  ),
                  const SizedBox(width: _windowButtonGap),
                  const PclIconButton(
                    icon: 'box-select',
                    size: PclMetrics.windowButtonSize,
                    tooltip: '最大化 / 还原：由系统窗口按钮接管',
                  ),
                  const SizedBox(width: _windowButtonGap),
                  // 关闭按钮有真实行为：SystemNavigator.pop() 关闭当前应用
                  // （规格 §6 允许的两种做法之一）。
                  PclIconButton(
                    icon: 'x',
                    size: PclMetrics.windowButtonSize,
                    tooltip: '关闭窗口',
                    onPressed: () => SystemNavigator.pop(),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// 自绘应用 Logo：半透明白的圆角渐变方块 + lucide `box` 图标 + 白色应用名。
///
/// 为什么自绘：规格 §0.2 明令不得使用 PCL-CE 的任何图片资源，图标一律用 MIT
/// 许可的 lucide；`assets/lucide/box.svg` 由 [PclIcon] 加载，不会崩（资源缺失时
/// 退化为同尺寸占位图标）。
class _AppLogo extends StatelessWidget {
  const _AppLogo();

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        Container(
          width: _logoBoxSize,
          height: _logoBoxSize,
          // 半透明白渐变：压在蓝色标题栏渐变上形成「磨砂」层次，
          // 比再叠一层实心蓝更能凸显 Logo 方块本身。
          decoration: BoxDecoration(
            gradient: const LinearGradient(
              begin: Alignment.topLeft,
              end: Alignment.bottomRight,
              colors: <Color>[Color(0x59FFFFFF), Color(0x1FFFFFFF)],
            ),
            // 圆角 6（规格 §2 图标圆角）
            borderRadius: BorderRadius.circular(PclMetrics.cardIconRadius),
            border: Border.all(color: const Color(0x66FFFFFF)),
          ),
          child: const Center(
            child: PclIcon(name: 'box', size: 16, color: Colors.white),
          ),
        ),
        const SizedBox(width: _logoTextGap),
        // 15 号白色文字（规格 §2「二级页标题栏内文字字号 15，白色」）
        const Text('UMango', style: PclTypography.pageTitleBarStyle),
      ],
    );
  }
}
