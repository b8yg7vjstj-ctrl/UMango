/// UMango 的 PCL-CE 风格组件原语（纯自绘 + Flutter 内置控件，不新增依赖）。
///
/// 本文件是 `docs/DESIGN_PCL_CE.md` 第 5 节的实现：命名、尺寸、字号与颜色 token
/// 全部照抄该规格（对应当前 PCL-CE 的 `MyButton` / `MyListItem` / `MyTag` 等
/// 控件的观感数值），**未复制其任何源码、XAML 文本或图片资源**。
/// 图标统一使用 MIT 许可的 **lucide**，从 `assets/lucide/<name>.svg` 加载并以
/// `ColorFilter.mode(color, BlendMode.srcIn)` 着色（lucide 是 `stroke="currentColor"`
/// 的单色描边图，srcIn 着色等价于把 currentColor 定为目标色）。
///
/// 依赖约束：只依赖 `../theme/pcl_theme.dart`、`package:flutter/material.dart` 与
/// `package:flutter_svg/flutter_svg.dart`；**不 import 任何 `pages/` 下的文件**。
///
/// 契约数值一览（详见各组件 doc comment）：
///  · 按钮 25/30 高、圆角 3、1px 描边、字号 13；
///  · 图标按钮 25（或 28）、圆角 4、hover 底色 `c6`；
///  · 列表项 36 高、选中 = `c6` 底 + `c2` 字 + 左侧 3px `c3` 竖条；
///  · 分组标题 12 字号 / 0.6 透明度 / 外边距 13,10,5,4；
///  · 标签按钮 27 高、水平外边距 5、内边距 2,0、选中态 2px 白色指示条；
///  · 小标签 `c6` 底 + `c2` 字 + 圆角 3 + 11 字号 + 内边距 6,2。
library;

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

import '../theme/pcl_theme.dart';

// ---------------------------------------------------------------------------
// 规格数值（集中在文件顶部，方便与 docs/DESIGN_PCL_CE.md 对照）
// ---------------------------------------------------------------------------

/// 按钮高度：默认 30（`MyButton`）。
const double _buttonHeight = 30;

/// 按钮高度：`dense: true` 时 25。
const double _buttonDenseHeight = 25;

/// 按钮水平内边距：默认 12。
const double _buttonPaddingX = 12;

/// 按钮水平内边距：`dense: true` 时 8。
const double _buttonDensePaddingX = 8;

/// 按钮内左侧图标尺寸（元信息图标为 12，按钮内略大以保持可读）。
const double _buttonIconSize = 14;

/// 按钮内图标与文字的间距。
const double _buttonIconGap = 6;

/// `onPressed == null` 时整体不透明度 0.45。
const double _disabledOpacity = 0.45;

/// 图标按钮的小尺寸：25 × 25（卡片操作按钮）。
const double _iconButtonSmallSize = 25;

/// 图标按钮小尺寸时字形大小。
const double _iconButtonGlyphSmall = 14;

/// 图标按钮大尺寸（28 × 28，标题栏窗口按钮）时字形大小。
const double _iconButtonGlyphLarge = 16;

/// 图标按钮圆角 4。
const double _iconButtonRadius = 4;

/// 列表项：选中态左侧指示条宽度 3（颜色 `c3`）。
const double _listItemIndicatorWidth = 3;

/// 列表项内容左边距 10 —— 叠加 3px 指示条后总缩进为 13，
/// 与 [PclGroupLabel] 的外边距 13 对齐，选中/未选中不会左右跳动。
const double _listItemContentPaddingLeft = 10;

/// 列表项内容右边距 8（`trailing` 的右边距）。
const double _listItemContentPaddingRight = 8;

/// 列表项图标尺寸 16。
const double _listItemIconSize = 16;

/// 列表项图标与文字间距 10。
const double _listItemIconGap = 10;

/// 列表项 badge 与文字间距 6。
const double _listItemBadgeGap = 6;

/// badge 圆角 3。
const double _badgeRadius = 3;

/// badge 水平内边距。
const double _badgePaddingX = 5;

/// badge 垂直内边距。
const double _badgePaddingY = 1;

/// 标签按钮水平外边距 5。
const double _tabHorizontalMargin = 5;

/// 标签按钮内边距 2,0。
const double _tabInnerPaddingX = 2;

/// 标签按钮选中指示条高度 2。
const double _tabIndicatorHeight = 2;

/// 标签按钮选中指示条圆角 1。
const double _tabIndicatorRadius = 1;

/// 标签按钮文字与指示条之间的间距。
const double _tabIndicatorGap = 2;

/// 小标签圆角 3。
const double _tagRadius = 3;

/// 小标签水平内边距 6。
const double _tagPaddingX = 6;

/// 小标签垂直内边距 2。
const double _tagPaddingY = 2;

// ---------------------------------------------------------------------------
// 组件
// ---------------------------------------------------------------------------

/// PCL-CE 风格的面板容器（对应面板/卡片类容器）：
/// 底色默认 [PclColors.semiWhite]（`#bbffffff`，半透明白）、圆角默认
/// [PclMetrics.windowRadius]（**6**），子内容按圆角裁剪；`color` / `radius` /
/// `padding` / `border` 均可覆盖（描边示例：`Border.all(color: PclColors.gray5)`）。
class PclPanel extends StatelessWidget {
  /// 创建面板容器。
  const PclPanel({
    super.key,
    required this.child,
    this.padding,
    this.radius,
    this.color,
    this.border,
  });

  /// 面板内容。
  final Widget child;

  /// 内边距，默认无。
  final EdgeInsetsGeometry? padding;

  /// 圆角，默认 [PclMetrics.windowRadius]（6）。
  final double? radius;

  /// 底色，默认 [PclColors.semiWhite]（半透明白）；传 `Colors.white` 即实心白底。
  final Color? color;

  /// 可选描边。
  final BoxBorder? border;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: padding,
      clipBehavior: Clip.antiAlias,
      decoration: BoxDecoration(
        color: color ?? PclColors.semiWhite,
        borderRadius: BorderRadius.circular(radius ?? PclMetrics.windowRadius),
        border: border,
      ),
      child: child,
    );
  }
}

/// PCL 风格按钮（对应 PCL-CE 的 `MyButton`）：
/// 圆角 [PclMetrics.buttonRadius]（**3**）、[PclMetrics.buttonBorderWidth]（**1px**）
/// 描边 [PclColors.c1]、底色 [PclColors.halfWhite]、字号 [PclTypography.body]（**13**）
/// 且文字色 = 描边色。
///
/// * `primary: true`：底色 [PclColors.c3]、白色文字、描边 [PclColors.c3]；
/// * 高度：`dense` **25** / 默认 **30**；水平内边距：`dense` **8** / 默认 **12**；
/// * 悬停（MouseRegion）底色 [PclColors.c6]；主按钮悬停用 [PclColors.c4]，
///   避免白字压在 `c6` 浅蓝上不可读；
/// * `onPressed == null` 时整体 [Opacity] **0.45**，且不响应点击与悬停；
/// * 使用 [GestureDetector]，没有 Material 水波纹。
class PclButton extends StatelessWidget {
  /// 创建按钮。
  const PclButton({
    super.key,
    required this.label,
    this.onPressed,
    this.icon,
    this.primary = false,
    this.dense = false,
  });

  /// 按钮文字。
  final String label;

  /// 点击回调；为 `null` 表示禁用（整体 0.45 不透明度且不可点）。
  final VoidCallback? onPressed;

  /// 文字左侧的 lucide 图标名（见 [PclIcon]），为空则不显示图标。
  final String? icon;

  /// 主按钮样式（`c3` 底 + 白字）。
  final bool primary;

  /// 紧凑尺寸（高 25、水平内边距 8）。
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final bool enabled = onPressed != null;
    final Color borderColor = primary ? PclColors.c3 : PclColors.c1;
    final Color textColor = primary ? Colors.white : borderColor;
    final double height = dense ? _buttonDenseHeight : _buttonHeight;
    final EdgeInsetsGeometry padding = EdgeInsets.symmetric(
      horizontal: dense ? _buttonDensePaddingX : _buttonPaddingX,
    );
    final String? iconName = icon;

    final Widget button = _HoverRegion(
      enabled: enabled,
      builder: (BuildContext context, bool hovered) {
        final Color background = hovered
            ? (primary ? PclColors.c4 : PclColors.c6)
            : (primary ? PclColors.c3 : PclColors.halfWhite);
        return GestureDetector(
          onTap: enabled ? onPressed : null,
          behavior: HitTestBehavior.opaque,
          child: Container(
            height: height,
            padding: padding,
            decoration: BoxDecoration(
              color: background,
              borderRadius: BorderRadius.circular(PclMetrics.buttonRadius),
              border: Border.all(
                color: borderColor,
                width: PclMetrics.buttonBorderWidth,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: <Widget>[
                if (iconName != null && iconName.isNotEmpty) ...<Widget>[
                  PclIcon(
                    name: iconName,
                    size: _buttonIconSize,
                    color: textColor,
                  ),
                  const SizedBox(width: _buttonIconGap),
                ],
                Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: PclTypography.body,
                    color: textColor,
                    fontFamilyFallback: PclTypography.fontFallback,
                  ),
                ),
              ],
            ),
          ),
        );
      },
    );

    if (enabled) {
      return button;
    }
    return Opacity(opacity: _disabledOpacity, child: button);
  }
}

/// 图标按钮（对应 PCL-CE 的 `MyButton` 图标形态 / 标题栏窗口按钮）：
/// 正方形边长 [size] 默认 **25**（卡片操作按钮），也支持 **28**（标题栏窗口按钮）；
/// 圆角 **4**，悬停（MouseRegion）底色 [PclColors.c6]；`tooltip` 非空时用 [Tooltip] 包裹。
///
/// 字形颜色：契约没有给这个组件颜色参数，因此取上层 [IconTheme] 的 `color`
/// （标题栏可用 `IconTheme(data: IconThemeData(color: Colors.white), child: ...)`
/// 让按钮在蓝色渐变上显示白字），缺省回落到 [PclColors.c1]。
/// `onPressed == null` 时整体 0.45 不透明度、不可点、无悬停反馈。
class PclIconButton extends StatefulWidget {
  /// 创建图标按钮。
  const PclIconButton({
    super.key,
    required this.icon,
    this.onPressed,
    this.size = 25,
    this.tooltip,
  });

  /// lucide 图标名（见 [PclIcon]）。
  final String icon;

  /// 点击回调；为 `null` 表示禁用。
  final VoidCallback? onPressed;

  /// 正方形边长，默认 25，标题栏窗口按钮用 28。
  final double size;

  /// 可选提示文字。
  final String? tooltip;

  @override
  State<PclIconButton> createState() => _PclIconButtonState();
}

class _PclIconButtonState extends State<PclIconButton> {
  bool _hovered = false;

  bool get _enabled => widget.onPressed != null;

  @override
  Widget build(BuildContext context) {
    final bool enabled = _enabled;
    final Color iconColor = IconTheme.of(context).color ?? PclColors.c1;
    final double glyphSize = widget.size <= _iconButtonSmallSize
        ? _iconButtonGlyphSmall
        : _iconButtonGlyphLarge;

    Widget button = MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
      onEnter: enabled ? (_) => _setHovered(true) : null,
      onExit: enabled ? (_) => _setHovered(false) : null,
      child: GestureDetector(
        onTap: enabled ? widget.onPressed : null,
        behavior: HitTestBehavior.opaque,
        child: Container(
          width: widget.size,
          height: widget.size,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: (enabled && _hovered) ? PclColors.c6 : Colors.transparent,
            borderRadius: BorderRadius.circular(_iconButtonRadius),
          ),
          child: PclIcon(
            name: widget.icon,
            size: glyphSize,
            color: iconColor,
          ),
        ),
      ),
    );

    if (!enabled) {
      button = Opacity(opacity: _disabledOpacity, child: button);
    }

    final String? tooltip = widget.tooltip;
    if (tooltip != null && tooltip.isNotEmpty) {
      button = Tooltip(message: tooltip, child: button);
    }
    return button;
  }

  void _setHovered(bool value) {
    if (!mounted || _hovered == value) {
      return;
    }
    setState(() => _hovered = value);
  }
}

/// lucide 图标：按 [name] 从 `assets/lucide/<name>.svg` 加载，并以
/// `ColorFilter.mode(color, BlendMode.srcIn)` 着色（lucide 为单色描边图）。
///
/// * [size] 默认 **16**（左栏列表项与元信息图标的规格尺寸）；
/// * 颜色缺省为 [PclColors.c1]（PCL 主文字色）；
/// * [strokeWidth] 覆盖 lucide 的 `stroke-width="2"`（flutter_svg 没有现成参数，
///   故改为读取 SVG 源码后改写根节点属性再渲染）；
/// * **资源缺失不会崩**：加载/解析失败一律退化为同尺寸 [Icon]`(Icons.help_outline)`；
///   注意 `assets/lucide/` 需要登记在 `pubspec.yaml` 的 `assets:` 下才能被加载。
class PclIcon extends StatelessWidget {
  /// 创建图标。
  const PclIcon({
    super.key,
    required this.name,
    this.size = 16,
    this.color,
    this.strokeWidth,
  });

  /// lucide 图标名（不含路径与扩展名），例如 `package`、`search`。
  final String name;

  /// 边长，默认 16。
  final double size;

  /// 颜色，默认 [PclColors.c1]。
  final Color? color;

  /// 覆盖描边宽度，`null` 时保持 lucide 原值。
  final double? strokeWidth;

  /// lucide 资源目录（相对 `ui/`）。
  static const String assetDirectory = 'assets/lucide';

  /// 由图标名得到资源路径：`assets/lucide/<name>.svg`。
  static String assetPathFor(String name) {
    var normalized = name.trim();
    if (normalized.toLowerCase().endsWith('.svg')) {
      normalized = normalized.substring(0, normalized.length - 4);
    }
    while (normalized.startsWith('/')) {
      normalized = normalized.substring(1);
    }
    return '$assetDirectory/$normalized.svg';
  }

  @override
  Widget build(BuildContext context) {
    final Color resolvedColor = color ?? PclColors.c1;
    final Widget fallback = Icon(
      Icons.help_outline,
      size: size,
      color: resolvedColor,
    );
    final String iconName = name.trim();
    if (iconName.isEmpty) {
      return fallback;
    }

    final String assetPath = assetPathFor(iconName);
    final ColorFilter colorFilter = ColorFilter.mode(
      resolvedColor,
      BlendMode.srcIn,
    );
    final Widget placeholder = SizedBox(width: size, height: size);
    final double? stroke = strokeWidth;

    if (stroke != null && stroke > 0) {
      return _StrokeWidthIcon(
        assetPath: assetPath,
        size: size,
        colorFilter: colorFilter,
        strokeWidth: stroke,
        fallback: fallback,
      );
    }

    return SvgPicture.asset(
      assetPath,
      width: size,
      height: size,
      fit: BoxFit.contain,
      colorFilter: colorFilter,
      placeholderBuilder: (_) => placeholder,
      errorBuilder: (_, _, _) => fallback,
    );
  }
}

/// 左栏列表项（对应 PCL-CE 的 `MyListItem`）：
/// 高度 [PclMetrics.listItemHeight]（**36**）。
///
/// * 选中态：底色 [PclColors.c6] + 文字与图标 [PclColors.c2] + 左侧 **3px** 宽
///   [PclColors.c3] 竖条（未选中时该位置的透明条保留，避免文字左右跳动）；
/// * 未选中悬停：底色 [PclColors.gray8]；
/// * 图标 **16**（[PclIcon]）、图标与文字间距 **10**、字号 13（[PclTypography.body]）；
/// * [trailing] 右边距 **8**；[badge] 存在时在文字右侧显示 [PclColors.c3] 底色的
///   小圆角标记（圆角 3、字号 11、白字）。
class PclListItem extends StatelessWidget {
  /// 创建列表项。
  const PclListItem({
    super.key,
    required this.label,
    this.icon,
    this.selected = false,
    this.onTap,
    this.trailing,
    this.badge,
  });

  /// 主文字。
  final String label;

  /// 左侧 lucide 图标名（见 [PclIcon]）。
  final String? icon;

  /// 是否选中。
  final bool selected;

  /// 点击回调。
  final VoidCallback? onTap;

  /// 右侧附加内容（如展开箭头、开关）。
  final Widget? trailing;

  /// 文字右侧的小标记文字（通常是数字），如未读数。
  final String? badge;

  @override
  Widget build(BuildContext context) {
    final Color foreground = selected ? PclColors.c2 : PclColors.c1;
    final String? iconName = icon;
    final String? badgeText = badge;
    final Widget? trailingChild = trailing;

    return _HoverRegion(
      builder: (BuildContext context, bool hovered) {
        final Color background = selected
            ? PclColors.c6
            : (hovered ? PclColors.gray8 : Colors.transparent);
        return GestureDetector(
          onTap: onTap,
          behavior: HitTestBehavior.opaque,
          child: Container(
            height: PclMetrics.listItemHeight,
            color: background,
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: <Widget>[
                // 选中态左侧 3px 竖条：未选中时保留同宽透明占位。
                Container(
                  width: _listItemIndicatorWidth,
                  color: selected ? PclColors.c3 : Colors.transparent,
                ),
                Expanded(
                  child: Padding(
                    padding: const EdgeInsets.only(
                      left: _listItemContentPaddingLeft,
                      right: _listItemContentPaddingRight,
                    ),
                    child: Row(
                      children: <Widget>[
                        Expanded(
                          child: Row(
                            children: <Widget>[
                              if (iconName != null && iconName.isNotEmpty) ...<
                                Widget
                              >[
                                PclIcon(
                                  name: iconName,
                                  size: _listItemIconSize,
                                  color: foreground,
                                ),
                                const SizedBox(width: _listItemIconGap),
                              ],
                              Flexible(
                                child: Text(
                                  label,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: TextStyle(
                                    fontSize: PclTypography.body,
                                    color: foreground,
                                    fontFamilyFallback:
                                        PclTypography.fontFallback,
                                  ),
                                ),
                              ),
                              if (badgeText != null &&
                                  badgeText.isNotEmpty) ...<Widget>[
                                const SizedBox(width: _listItemBadgeGap),
                                _PclBadge(text: badgeText),
                              ],
                            ],
                          ),
                        ),
                        // 空安全元素：trailing 为空时该项直接省略。
                        ?trailingChild,
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}

/// 分组标题（PCL-CE 左栏的分组头）：
/// 字号 [PclTypography.caption]（**12**）、[PclColors.c1] 叠加 **0.6** 不透明度、
/// 外边距 `EdgeInsets.fromLTRB(13, 10, 5, 4)`。
class PclGroupLabel extends StatelessWidget {
  /// 创建分组标题。
  const PclGroupLabel({super.key, required this.text});

  /// 标题文字。
  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(13, 10, 5, 4),
      child: Text(
        text,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: PclTypography.caption,
          color: PclColors.c1.withValues(alpha: 0.6),
          fontFamilyFallback: PclTypography.fontFallback,
        ),
      ),
    );
  }
}

/// 顶部标签按钮（对应 PCL-CE 标题栏的 RadioButton 标签）：
/// 高度 [PclMetrics.tabHeight]（**27**）、水平外边距 **5**、内边距 **2,0**、
/// 字号 [PclTypography.body]（**13**）、字重 `w600`。
///
/// * 选中态：白色文字 + 下方 **2px** 白色指示条（圆角 **1**）；
/// * 未选中：`white70`；悬停：`white`；
/// * 用 [GestureDetector]（无水波纹），指示条宽度跟随文字宽度（[IntrinsicWidth]）。
class PclTabButton extends StatelessWidget {
  /// 创建标签按钮。
  const PclTabButton({
    super.key,
    required this.label,
    required this.selected,
    this.onTap,
  });

  /// 标签文字。
  final String label;

  /// 是否选中。
  final bool selected;

  /// 点击回调。
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: _tabHorizontalMargin),
      child: _HoverRegion(
        builder: (BuildContext context, bool hovered) {
          final Color foreground = selected
              ? Colors.white
              : (hovered ? Colors.white : Colors.white70);
          return GestureDetector(
            onTap: onTap,
            behavior: HitTestBehavior.opaque,
            child: SizedBox(
              height: PclMetrics.tabHeight,
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: _tabInnerPaddingX,
                ),
                child: IntrinsicWidth(
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.end,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: <Widget>[
                      Text(
                        label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          fontSize: PclTypography.body,
                          fontWeight: FontWeight.w600,
                          color: foreground,
                          fontFamilyFallback: PclTypography.fontFallback,
                        ),
                      ),
                      const SizedBox(height: _tabIndicatorGap),
                      Container(
                        height: _tabIndicatorHeight,
                        decoration: BoxDecoration(
                          color: selected ? Colors.white : Colors.transparent,
                          borderRadius: BorderRadius.circular(
                            _tabIndicatorRadius,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

/// 小标签（对应 PCL-CE 的 `MyTag`）：
/// 底色 [PclColors.c6]、文字 [PclColors.c2]、圆角 **3**、字号
/// [PclTypography.label]（**11**）、内边距 **6,2**。
/// [color] 覆盖文字色、[background] 覆盖底色（例如错误标签传
/// `background: PclColors.redBack, color: PclColors.redDark`）。
class PclTag extends StatelessWidget {
  /// 创建标签。
  const PclTag({super.key, required this.text, this.color, this.background});

  /// 标签文字。
  final String text;

  /// 文字色，默认 [PclColors.c2]。
  final Color? color;

  /// 底色，默认 [PclColors.c6]。
  final Color? background;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(
        horizontal: _tagPaddingX,
        vertical: _tagPaddingY,
      ),
      decoration: BoxDecoration(
        color: background ?? PclColors.c6,
        borderRadius: BorderRadius.circular(_tagRadius),
      ),
      child: Text(
        text,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: PclTypography.label,
          color: color ?? PclColors.c2,
          fontFamilyFallback: PclTypography.fontFallback,
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// 私有辅助
// ---------------------------------------------------------------------------

/// 悬停侦测器：把“是否悬停”这一局部状态收敛在私有 [State] 里，
/// 让公开组件（[PclButton]、[PclListItem]、[PclTabButton]）保持契约声明的
/// `StatelessWidget` 类型，同时拿到 MouseRegion 的 hover 信息。
class _HoverRegion extends StatefulWidget {
  const _HoverRegion({required this.builder, this.enabled = true});

  /// 依据 hover 状态构建子树。
  final Widget Function(BuildContext context, bool hovered) builder;

  /// 是否响应悬停（禁用时恒为 false，且不改光标）。
  final bool enabled;

  @override
  State<_HoverRegion> createState() => _HoverRegionState();
}

class _HoverRegionState extends State<_HoverRegion> {
  bool _hovered = false;

  void _setHovered(bool value) {
    if (!widget.enabled || _hovered == value) {
      return;
    }
    setState(() => _hovered = value);
  }

  @override
  Widget build(BuildContext context) {
    final bool enabled = widget.enabled;
    return MouseRegion(
      cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
      onEnter: enabled ? (_) => _setHovered(true) : null,
      onExit: enabled ? (_) => _setHovered(false) : null,
      child: widget.builder(context, enabled && _hovered),
    );
  }
}

/// 需要覆盖描边宽度时的图标实现：flutter_svg 没有暴露 `stroke-width` 参数，
/// 因此读取 SVG 源码、改写根节点属性后交给 [SvgPicture.string] 渲染。
/// 读取（资源不存在）或解析失败一律退化为 [fallback]，不会抛异常。
class _StrokeWidthIcon extends StatelessWidget {
  const _StrokeWidthIcon({
    required this.assetPath,
    required this.size,
    required this.colorFilter,
    required this.strokeWidth,
    required this.fallback,
  });

  final String assetPath;
  final double size;
  final ColorFilter colorFilter;
  final double strokeWidth;
  final Widget fallback;

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<String>(
      future: DefaultAssetBundle.of(context).loadString(assetPath),
      builder: (BuildContext context, AsyncSnapshot<String> snapshot) {
        if (snapshot.hasError) {
          return fallback;
        }
        final String? source = snapshot.data;
        if (source == null) {
          return SizedBox(width: size, height: size);
        }
        return SvgPicture.string(
          _withStrokeWidth(source, strokeWidth),
          width: size,
          height: size,
          fit: BoxFit.contain,
          colorFilter: colorFilter,
          placeholderBuilder: (_) => SizedBox(width: size, height: size),
          errorBuilder: (_, _, _) => fallback,
        );
      },
    );
  }
}

/// 列表项右下角的小标记（badge）：[PclColors.c3] 底 + 白字 + 圆角 3 + 字号 11。
class _PclBadge extends StatelessWidget {
  const _PclBadge({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(
        horizontal: _badgePaddingX,
        vertical: _badgePaddingY,
      ),
      decoration: BoxDecoration(
        color: PclColors.c3,
        borderRadius: BorderRadius.circular(_badgeRadius),
      ),
      child: Text(
        text,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          fontSize: PclTypography.label,
          color: Colors.white,
          fontFamilyFallback: PclTypography.fontFallback,
        ),
      ),
    );
  }
}

/// 把 SVG 根节点的 `stroke-width` 改写为 [strokeWidth]（没有该属性则插入）。
/// 找不到根节点时原样返回，绝不抛异常。
String _withStrokeWidth(String svgSource, double strokeWidth) {
  final String value = strokeWidth == strokeWidth.roundToDouble()
      ? strokeWidth.round().toString()
      : strokeWidth.toString();
  final RegExp rootTag = RegExp(r'<svg\b[^>]*>', caseSensitive: false);
  final Match? match = rootTag.firstMatch(svgSource);
  if (match == null) {
    return svgSource;
  }

  String tag = match.group(0)!;
  final RegExp existing = RegExp(
    r'''stroke-width\s*=\s*("[^"]*"|'[^']*')''',
    caseSensitive: false,
  );
  if (existing.hasMatch(tag)) {
    tag = tag.replaceFirst(existing, 'stroke-width="$value"');
  } else {
    final int insertAt = tag.endsWith('/>') ? tag.length - 2 : tag.length - 1;
    tag =
        '${tag.substring(0, insertAt)} stroke-width="$value"'
        '${tag.substring(insertAt)}';
  }
  return svgSource.replaceRange(match.start, match.end, tag);
}
