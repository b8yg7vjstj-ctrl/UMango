/// PCL-CE 设计 token 的 Flutter 落地实现。
///
/// 本文件的**唯一依据**是 `docs/DESIGN_PCL_CE.md`（第 1/2/3/4 节）：
/// 颜色、尺寸、字号全部照抄该规格中的数值，命名严格遵循第 4 节的 API 契约。
/// 规格数值提取自 PCL-CE（`PCL-Community/PCL-CE`，dev 分支）的 XAML 资源定义，
/// 但本项目**未复制**其任何源码、XAML 文本或图片资源，全部代码自行编写。
///
/// 约束：纯 Dart，只依赖 `package:flutter/material.dart`，不 import 项目内其他文件。
library;

import 'package:flutter/material.dart';

/// 颜色 token —— 对应 PCL-CE `Application.xaml` 资源字典中的颜色定义。
///
/// 命名（c1..c8 / bg0 / bg1 / gray1..gray8 / …）与 `DESIGN_PCL_CE.md` 第 1 节一一对应，
/// 不得改名；数值即文档中的 hex 值。
class PclColors {
  const PclColors._();

  // ---------------------------------------------------------------------------
  // 主色阶（蓝色系）—— 规格 §1「主色阶」
  // ---------------------------------------------------------------------------

  /// `#343d4a`：主文字色 / 按钮描边。
  static const Color c1 = Color(0xFF343D4A);

  /// `#0b5bcb`：深蓝，强调文字（如标签文字）。
  static const Color c2 = Color(0xFF0B5BCB);

  /// `#1370f3`：**主强调色**（选中态、主按钮），也是 ColorScheme.primary。
  static const Color c3 = Color(0xFF1370F3);

  /// `#4890f5`：中蓝。
  static const Color c4 = Color(0xFF4890F5);

  /// `#96c0f9`：浅蓝。
  static const Color c5 = Color(0xFF96C0F9);

  /// `#d5e6fd`：极浅蓝（标签底、hover 底色）。
  static const Color c6 = Color(0xFFD5E6FD);

  /// `#e0eafd`：极浅蓝。
  static const Color c7 = Color(0xFFE0EAFD);

  /// `#eaf2fe`：最浅蓝（面板底）。
  static const Color c8 = Color(0xFFEAF2FE);

  /// `#96c0f9`：标题栏渐变端色（与 c5 同值，规格中单列）。
  static const Color bg0 = Color(0xFF96C0F9);

  /// `#bee0eafd`：半透明面板底（alpha = 0xBE，色值 = c7 的 #e0eafd）。
  static const Color bg1 = Color(0xBEE0EAFD);

  // ---------------------------------------------------------------------------
  // 灰阶 —— 规格 §1「灰阶」
  // ---------------------------------------------------------------------------

  /// `#404040`：主要文字。
  static const Color gray1 = Color(0xFF404040);

  /// `#737373`：次级文字。
  static const Color gray2 = Color(0xFF737373);

  /// `#8c8c8c`：说明文字 / 元信息。
  static const Color gray3 = Color(0xFF8C8C8C);

  /// `#a6a6a6`：更淡的元信息。
  static const Color gray4 = Color(0xFFA6A6A6);

  /// `#cccccc`：分隔线 / 描边。
  static const Color gray5 = Color(0xFFCCCCCC);

  /// `#ebebeb`：浅分隔。
  static const Color gray6 = Color(0xFFEBEBEB);

  /// `#f0f0f0`：浅底。
  static const Color gray7 = Color(0xFFF0F0F0);

  /// `#f5f5f5`：更浅底。
  static const Color gray8 = Color(0xFFF5F5F5);

  // ---------------------------------------------------------------------------
  // 语义色（红）—— 规格 §1「语义色」
  // ---------------------------------------------------------------------------

  /// `#ff4c4c`：亮红（危险操作 / hover 红底）。
  static const Color redLight = Color(0xFFFF4C4C);

  /// `#ce2111`：深红（危险操作按下态 / 红底上的文字）。
  static const Color redDark = Color(0xFFCE2111);

  /// `#fbdddd`：红底（错误面板背景）。
  static const Color redBack = Color(0xFFFBDDDD);

  /// `#c23616`：致命错误。
  static const Color fatal = Color(0xFFC23616);

  /// `#e74c3c`：一般错误。
  static const Color error = Color(0xFFE74C3C);

  // ---------------------------------------------------------------------------
  // 背景与透明 —— 规格 §1「背景与透明」
  // ---------------------------------------------------------------------------

  /// `#fbfbfb`：窗口背景（scaffoldBackgroundColor）。
  static const Color background = Color(0xFFFBFBFB);

  /// `#55ffffff`：按钮底（35% 半透明白，叠在任意底色上都保留 PCL 的“毛玻璃”观感）。
  static const Color halfWhite = Color(0x55FFFFFF);

  /// `#bbffffff`：卡片底（73% 半透明白）。
  static const Color semiWhite = Color(0xBBFFFFFF);

  /// `#e5ffffff`：提示框底（90% 半透明白）。
  static const Color tooltip = Color(0xE5FFFFFF);

  /// `#d2fbfbfb`：半透明窗口底（82% 不透明的 background）。
  static const Color transparentBackground = Color(0xD2FBFBFB);

  // ---------------------------------------------------------------------------
  // 由规格 §2 的透明度派生出的文字色（文档只给了「色 + 透明度」，此处预乘成 ARGB）
  // ---------------------------------------------------------------------------

  /// 分组标题文字：gray3 叠加 0.6 透明度（规格 §2：分组标题透明度 0.6）。
  static const Color groupLabelText = Color(0x998C8C8C);

  /// 卡片副标题（原始包名）：gray1 叠加 0.4 透明度（规格 §2：原始名透明度 0.4）。
  static const Color cardSubtitleText = Color(0x66404040);

  /// 窗口/卡片阴影色（规格未给值，取 20% 黑以保证桌面端的克制观感）。
  static const Color shadow = Color(0x33000000);

  // ---------------------------------------------------------------------------
  // 渐变
  // ---------------------------------------------------------------------------

  /// 标题栏横向渐变：c3 → c4 → bg0(c5)。
  ///
  /// 规格 §4 要求「c3 -> c4 -> c5 类似观感」，且 §6 要求观感接近 PCL 的蓝色标题栏；
  /// 因此不使用任何 Material 默认色，三段全部取自主色阶。
  static const LinearGradient titleBarGradient = LinearGradient(
    begin: Alignment.centerLeft,
    end: Alignment.centerRight,
    colors: <Color>[c3, c4, bg0],
    stops: <double>[0.0, 0.55, 1.0],
  );

  /// 标题栏实际底色 —— **纯色**，不是渐变。
  ///
  /// 这是用 PCL-CE 真实运行录屏逐像素验证出来的结论：
  /// 在标题栏横向取 11 个采样点（x=0/110/…/1100），除最左最右两个受窗口圆角
  /// 影响的点外，其余 9 点颜色完全一致 —— 说明视觉上是纯色而非渐变。
  /// 因此外壳改用本常量；[titleBarGradient] 保留仅作参考。
  static const Color titleBar = c3;
}

/// 尺寸与度量常量 —— 对应 `DESIGN_PCL_CE.md` 第 2 节表格。
///
/// 所有数值的单位都是逻辑像素（与 PCL-CE 的 XAML 单位 1:1 对应）。
class PclMetrics {
  const PclMetrics._();

  // ---------------------------------------------------------------------------
  // 窗口与外壳（规格 §2「窗口与外壳」）
  // ---------------------------------------------------------------------------

  /// 窗口默认宽度 850。
  static const double windowDefaultWidth = 850.0;

  /// 窗口默认高度 500。
  static const double windowDefaultHeight = 500.0;

  /// 窗口最小宽度 810。
  static const double windowMinWidth = 810.0;

  /// 窗口最小高度 470。
  static const double windowMinHeight = 470.0;

  /// 窗口默认尺寸 850 × 500。
  static const Size windowDefaultSize = Size(850.0, 500.0);

  /// 窗口最小尺寸 810 × 470。
  static const Size windowMinSize = Size(810.0, 470.0);

  /// 外层边距（四周留白）10。
  static const double windowMargin = 10.0;

  /// 外层圆角 6。
  static const double windowRadius = 6.0;

  /// 顶部标题栏高度 48。
  static const double titleBarHeight = 48.0;

  /// 标题栏右侧窗口按钮尺寸 28 × 28。
  static const double windowButtonSize = 28.0;

  /// 窗口按钮右边距 12（三个按钮定位为 12 / 44 / 76）。
  static const double windowButtonRightMargin = 12.0;

  /// 窗口按钮之间的间距 32（28 + 4；12 / 44 / 76 的等差即 32）。
  static const double windowButtonSpacing = 32.0;

  /// 二级页返回按钮尺寸 28 × 28。
  static const double backButtonSize = 28.0;

  /// 二级页返回按钮左边距 12。
  static const double backButtonLeftMargin = 12.0;

  /// 左栏右侧阴影宽度 4。
  static const double leftShadowWidth = 4.0;

  /// 左栏宽度 168（规格 §6 外壳布局：可选左栏宽度 168）。
  static const double leftRailWidth = 168.0;

  // ---------------------------------------------------------------------------
  // 顶部标签按钮（规格 §2「窗口与外壳」中的 RadioButton 项）
  // ---------------------------------------------------------------------------

  /// 标题标签按钮高度 27。
  static const double tabHeight = 27.0;

  /// 标题标签按钮水平外边距 5。
  static const double tabMarginHorizontal = 5.0;

  /// 标题标签按钮内边距 2,0。
  static const EdgeInsets tabPadding = EdgeInsets.symmetric(horizontal: 2.0);

  // ---------------------------------------------------------------------------
  // 左栏列表项（规格 §2「左栏列表项 MyListItem」）
  // ---------------------------------------------------------------------------

  /// 列表项高度 36。
  static const double listItemHeight = 36.0;

  /// 分组标题外边距 13,10,5,4。
  static const EdgeInsets groupLabelMargin = EdgeInsets.fromLTRB(13.0, 10.0, 5.0, 4.0);

  /// 分组标题字号 12。
  static const double groupLabelFontSize = 12.0;

  /// 分组标题透明度 0.6。
  static const double groupLabelOpacity = 0.6;

  /// 列表项选中态左侧竖条宽度 3（规格 §5 PclListItem：左侧 3px c3 竖条）。
  static const double listItemIndicatorWidth = 3.0;

  // ---------------------------------------------------------------------------
  // 包卡片（规格 §2「包卡片 MyCompItem」）
  // ---------------------------------------------------------------------------

  /// 卡片高度 64。
  static const double cardHeight = 64.0;

  /// 卡片图标尺寸 50 × 50。
  static const double cardIconSize = 50.0;

  /// 卡片图标圆角 6。
  static const double cardIconRadius = 6.0;

  /// 卡片列布局 `7 / 50 / 8 / Auto / 1* / Auto` 的第 1 列（左侧留白）7。
  static const double cardPaddingLeft = 7.0;

  /// 卡片列布局的第 3 列（图标与文字之间的间隙）8。
  static const double cardIconTextGap = 8.0;

  /// 卡片行布局 `1* / 20 / 18 / 16 / 1.1*` 的第 2 行（标题行）高度 20。
  static const double cardTitleRowHeight = 20.0;

  /// 卡片行布局的第 3 行（简介行）高度 18。
  static const double cardDescRowHeight = 18.0;

  /// 卡片行布局的第 4 行（元信息行）高度 16。
  static const double cardMetaRowHeight = 16.0;

  /// 右侧操作按钮尺寸 25 × 25。
  static const double cardActionButtonSize = 25.0;

  /// 右侧操作按钮之间的间距 4。
  static const double cardActionSpacing = 4.0;

  /// 右侧操作按钮默认 Opacity 0（仅 hover 显示）。
  static const double cardActionIdleOpacity = 0.0;

  // ---------------------------------------------------------------------------
  // 按钮（规格 §2「按钮 MyButton」）
  // ---------------------------------------------------------------------------

  /// 按钮圆角 3。
  static const double buttonRadius = 3.0;

  /// 按钮描边宽度 1（描边色 = c1）。
  static const double buttonBorderWidth = 1.0;

  /// 按钮字号 13。
  static const double buttonFontSize = 13.0;

  /// 图标按钮尺寸 25 × 25（规格 §2：25 × 25 或 28 × 28）。
  static const double iconButtonSize = 25.0;

  /// 大号图标按钮尺寸 28 × 28（窗口按钮、返回按钮使用）。
  static const double iconButtonLargeSize = 28.0;

  /// 小标签圆角 3（规格 §5 PclTag：3 圆角）。
  static const double tagRadius = 3.0;

  // ---------------------------------------------------------------------------
  // 元信息（规格 §2「包卡片 MyCompItem」）
  // ---------------------------------------------------------------------------

  /// 元信息图标尺寸 12 × 12。
  static const double metaIconSize = 12.0;

  /// 元信息图标与文字的间距 4（规格为 4–5，取小值）。
  static const double metaIconGap = 4.0;
}

/// 字号与文本样式 —— 对应 `DESIGN_PCL_CE.md` 第 3 节「字体」。
///
/// 不使用 PCL-CE 自带的 `PCL English` 字体文件，改用系统字体回退链
/// [fontFallback]（规格 §3 原文：`PingFang SC` / `Microsoft YaHei UI` /
/// `Segoe UI` / `Helvetica Neue`）。
class PclTypography {
  const PclTypography._();

  // ---------------------------------------------------------------------------
  // 字号层级（规格 §3 / §4）
  // ---------------------------------------------------------------------------

  /// 11：标签（PclTag 等小标签）。
  static const double label = 11.0;

  /// 12：说明与元信息。
  static const double caption = 12.0;

  /// 13：按钮与正文。
  static const double body = 13.0;

  /// 14：卡片标题。
  static const double cardTitle = 14.0;

  /// 15：二级页标题（也是二级页标题栏内文字字号，白色）。
  static const double pageTitle = 15.0;

  /// 20：大标题（规格 §3：20+ 用于大标题）。
  static const double largeTitle = 20.0;

  /// 系统字体回退链（规格 §3 原文顺序）。
  static const List<String> fontFallback = <String>[
    'PingFang SC',
    'Microsoft YaHei UI',
    'Segoe UI',
    'Helvetica Neue',
  ];

  // ---------------------------------------------------------------------------
  // 便利 TextStyle：全部带 [fontFallback]；颜色取自 [PclColors] 的对应 token
  // ---------------------------------------------------------------------------

  /// 11 号标签文字（标签底 c6 + 文字 c2）。
  static const TextStyle labelStyle = TextStyle(
    fontSize: label,
    fontFamilyFallback: fontFallback,
    color: PclColors.c2,
    height: 1.2,
  );

  /// 12 号说明文字：gray3。
  static const TextStyle captionStyle = TextStyle(
    fontSize: caption,
    fontFamilyFallback: fontFallback,
    color: PclColors.gray3,
    height: 1.35,
  );

  /// 12 号元信息：gray3（版本号，规格 §2）。
  static const TextStyle versionMetaStyle = TextStyle(
    fontSize: caption,
    fontFamilyFallback: fontFallback,
    color: PclColors.gray3,
    height: 1.35,
  );

  /// 12 号元信息：gray4（下载量、时间、来源，规格 §2）。
  static const TextStyle metaStyle = TextStyle(
    fontSize: caption,
    fontFamilyFallback: fontFallback,
    color: PclColors.gray4,
    height: 1.35,
  );

  /// 12 号卡片副标题（原始包名）：gray1 叠加 0.4 透明度。
  static const TextStyle cardSubtitleStyle = TextStyle(
    fontSize: caption,
    fontFamilyFallback: fontFallback,
    color: PclColors.cardSubtitleText,
    height: 1.35,
  );

  /// 13 号正文：c1。
  static const TextStyle bodyStyle = TextStyle(
    fontSize: body,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    height: 1.4,
  );

  /// 13 号按钮文字：文字色 = 描边色 = c1。
  static const TextStyle buttonStyle = TextStyle(
    fontSize: body,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    height: 1.2,
  );

  /// 13 号左栏列表项文字。
  static const TextStyle listItemStyle = TextStyle(
    fontSize: body,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    height: 1.2,
  );

  /// 14 号卡片标题：c1。
  static const TextStyle cardTitleStyle = TextStyle(
    fontSize: cardTitle,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    height: 1.3,
  );

  /// 15 号二级页标题：c1。
  static const TextStyle pageTitleStyle = TextStyle(
    fontSize: pageTitle,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    fontWeight: FontWeight.w600,
    height: 1.3,
  );

  /// 15 号二级页标题栏内文字：白色（规格 §2）。
  static const TextStyle pageTitleBarStyle = TextStyle(
    fontSize: pageTitle,
    fontFamilyFallback: fontFallback,
    color: Colors.white,
    fontWeight: FontWeight.w600,
    height: 1.3,
  );

  /// 20 号大标题：c1。
  static const TextStyle largeTitleStyle = TextStyle(
    fontSize: largeTitle,
    fontFamilyFallback: fontFallback,
    color: PclColors.c1,
    fontWeight: FontWeight.w600,
    height: 1.25,
  );

  /// 12 号分组标题：gray3 叠加 0.6 透明度。
  static const TextStyle groupLabelStyle = TextStyle(
    fontSize: caption,
    fontFamilyFallback: fontFallback,
    color: PclColors.groupLabelText,
    height: 1.2,
  );

  /// 标签按钮未选中文字：半透明白（压在蓝色渐变标题栏上）。
  static const TextStyle tabUnselectedStyle = TextStyle(
    fontSize: body,
    fontFamilyFallback: fontFallback,
    color: Color(0xCCFFFFFF),
    height: 1.2,
  );

  /// 标签按钮选中文字：纯白。
  static const TextStyle tabSelectedStyle = TextStyle(
    fontSize: body,
    fontFamilyFallback: fontFallback,
    color: Colors.white,
    fontWeight: FontWeight.w600,
    height: 1.2,
  );

  /// Material 文本层级 → PCL 字号层级的映射，供 [buildPclTheme] 使用。
  static const TextTheme textTheme = TextTheme(
    displayLarge: largeTitleStyle,
    displayMedium: largeTitleStyle,
    displaySmall: pageTitleStyle,
    headlineLarge: pageTitleStyle,
    headlineMedium: cardTitleStyle,
    headlineSmall: cardTitleStyle,
    titleLarge: pageTitleStyle,
    titleMedium: cardTitleStyle,
    titleSmall: bodyStyle,
    bodyLarge: bodyStyle,
    bodyMedium: bodyStyle,
    bodySmall: captionStyle,
    labelLarge: buttonStyle,
    labelMedium: captionStyle,
    labelSmall: labelStyle,
  );
}

/// 构造 PCL 观感的 Material 3 主题。
///
/// 与规格 §4 的约定一致：
/// * 显式逐槽位构造 [ColorScheme]（不使用「种子色推导」式构造，避免带进 Material 的
///   紫/青色调），`primary` = [PclColors.c3]；
/// * `scaffoldBackgroundColor` = [PclColors.background]；
/// * 字体回退链 = [PclTypography.fontFallback]；
/// * 圆角与描边取自 [PclMetrics]（面板 6 / 按钮 3 / 描边 1px [PclColors.c1]）。
ThemeData buildPclTheme() {
  // 显式 ColorScheme：每个槽位都取自 PCL token，禁用种子色的色调推导。
  const ColorScheme scheme = ColorScheme(
    brightness: Brightness.light,
    // 主强调色
    primary: PclColors.c3,
    onPrimary: Colors.white,
    primaryContainer: PclColors.c6,
    onPrimaryContainer: PclColors.c2,
    // 次要强调色
    secondary: PclColors.c4,
    onSecondary: Colors.white,
    secondaryContainer: PclColors.c8,
    onSecondaryContainer: PclColors.c2,
    // 三级强调色（标题栏浅蓝）
    tertiary: PclColors.c5,
    onTertiary: PclColors.c1,
    tertiaryContainer: PclColors.c7,
    onTertiaryContainer: PclColors.c2,
    // 语义色
    error: PclColors.error,
    onError: Colors.white,
    errorContainer: PclColors.redBack,
    onErrorContainer: PclColors.redDark,
    // 表面：面板/卡片是纯白，窗口底是 background
    surface: Colors.white,
    onSurface: PclColors.c1,
    onSurfaceVariant: PclColors.gray2,
    surfaceDim: PclColors.gray7,
    surfaceBright: Colors.white,
    surfaceContainerLowest: Colors.white,
    surfaceContainerLow: PclColors.background,
    surfaceContainer: PclColors.gray8,
    surfaceContainerHigh: PclColors.gray7,
    surfaceContainerHighest: PclColors.gray6,
    // 描边
    outline: PclColors.gray5,
    outlineVariant: PclColors.gray6,
    shadow: PclColors.shadow,
    scrim: PclColors.shadow,
    // 反色（用于 SnackBar 等）：PCL 的深色即 c1
    inverseSurface: PclColors.c1,
    onInverseSurface: Colors.white,
    inversePrimary: PclColors.c5,
    surfaceTint: PclColors.c3,
  );

  // 面板圆角（6，规格 §2「外层圆角」）
  const BorderRadius panelRadius = BorderRadius.all(
    Radius.circular(PclMetrics.windowRadius),
  );

  // 按钮圆角（3，规格 §2「按钮 MyButton」）
  final BorderRadius buttonRadius = BorderRadius.circular(PclMetrics.buttonRadius);

  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    // 窗口背景 #fbfbfb
    scaffoldBackgroundColor: PclColors.background,
    canvasColor: PclColors.background,
    // 系统字体回退链（规格 §3）
    fontFamilyFallback: PclTypography.fontFallback,
    textTheme: PclTypography.textTheme,
    // 桌面端密度：紧凑，符合 PCL 的信息密度
    visualDensity: VisualDensity.compact,
    materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,

    // 分隔线：gray6 浅分隔，1px
    dividerTheme: const DividerThemeData(
      color: PclColors.gray6,
      thickness: 1.0,
      space: 1.0,
    ),

    // 面板/卡片：白底 + 6 圆角 + gray5 描边，无 M3 色调叠加
    cardTheme: const CardThemeData(
      color: Colors.white,
      surfaceTintColor: Colors.transparent,
      shadowColor: PclColors.shadow,
      elevation: 0.0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: panelRadius,
        side: BorderSide(color: PclColors.gray5, width: PclMetrics.buttonBorderWidth),
      ),
    ),

    // 对话框：白底 + 6 圆角，标题 15 / 正文 13
    dialogTheme: const DialogThemeData(
      backgroundColor: Colors.white,
      surfaceTintColor: Colors.transparent,
      elevation: 8.0,
      shape: RoundedRectangleBorder(borderRadius: panelRadius),
      titleTextStyle: PclTypography.pageTitleStyle,
      contentTextStyle: PclTypography.bodyStyle,
    ),

    // 按钮：13 字号 + c1 文字/描边 + 3 圆角 + halfWhite 底
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: PclColors.c3,
        foregroundColor: Colors.white,
        disabledBackgroundColor: PclColors.gray6,
        disabledForegroundColor: PclColors.gray3,
        textStyle: PclTypography.buttonStyle,
        shape: RoundedRectangleBorder(borderRadius: buttonRadius),
        padding: const EdgeInsets.symmetric(horizontal: 14.0, vertical: 8.0),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        backgroundColor: PclColors.halfWhite,
        foregroundColor: PclColors.c1,
        textStyle: PclTypography.buttonStyle,
        side: const BorderSide(
          color: PclColors.c1,
          width: PclMetrics.buttonBorderWidth,
        ),
        shape: RoundedRectangleBorder(borderRadius: buttonRadius),
        padding: const EdgeInsets.symmetric(horizontal: 14.0, vertical: 8.0),
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        foregroundColor: PclColors.c2,
        textStyle: PclTypography.buttonStyle,
        shape: RoundedRectangleBorder(borderRadius: buttonRadius),
        padding: const EdgeInsets.symmetric(horizontal: 10.0, vertical: 6.0),
      ),
    ),
    // 图标按钮：hover 底 c6（规格 §5 PclIconButton）
    iconButtonTheme: IconButtonThemeData(
      style: IconButton.styleFrom(
        foregroundColor: PclColors.c1,
        highlightColor: PclColors.c6,
        shape: RoundedRectangleBorder(borderRadius: buttonRadius),
      ),
    ),

    // 提示框：tooltip 底（#e5ffffff）+ 12 号 c1 文字
    tooltipTheme: TooltipThemeData(
      decoration: const BoxDecoration(
        color: PclColors.tooltip,
        borderRadius: panelRadius,
      ),
      textStyle: PclTypography.captionStyle.copyWith(color: PclColors.c1),
      padding: const EdgeInsets.symmetric(horizontal: 8.0, vertical: 4.0),
      waitDuration: const Duration(milliseconds: 400),
    ),

    // 标签栏：选中 = 白字 + c3 指示条（标题栏是蓝色渐变）
    tabBarTheme: const TabBarThemeData(
      labelColor: Colors.white,
      unselectedLabelColor: Color(0xCCFFFFFF),
      indicatorColor: Colors.white,
      indicatorSize: TabBarIndicatorSize.tab,
      dividerColor: Colors.transparent,
      labelStyle: PclTypography.tabSelectedStyle,
      unselectedLabelStyle: PclTypography.tabUnselectedStyle,
    ),

    // 输入框：白底 + gray5 描边 + 3 圆角，聚焦时 c3
    inputDecorationTheme: InputDecorationThemeData(
      isDense: true,
      filled: true,
      fillColor: Colors.white,
      contentPadding: const EdgeInsets.symmetric(horizontal: 8.0, vertical: 8.0),
      hintStyle: PclTypography.captionStyle,
      border: OutlineInputBorder(
        borderRadius: buttonRadius,
        borderSide: const BorderSide(color: PclColors.gray5),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: buttonRadius,
        borderSide: const BorderSide(color: PclColors.gray5),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: buttonRadius,
        borderSide: const BorderSide(color: PclColors.c3),
      ),
      errorBorder: OutlineInputBorder(
        borderRadius: buttonRadius,
        borderSide: const BorderSide(color: PclColors.error),
      ),
      focusedErrorBorder: OutlineInputBorder(
        borderRadius: buttonRadius,
        borderSide: const BorderSide(color: PclColors.error, width: 2.0),
      ),
    ),

    // 小标签：c6 底 + c2 文字 + 3 圆角 + 11 字号（规格 §5 PclTag）
    chipTheme: const ChipThemeData(
      backgroundColor: PclColors.c6,
      labelStyle: PclTypography.labelStyle,
      side: BorderSide.none,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.all(Radius.circular(PclMetrics.tagRadius)),
      ),
      padding: EdgeInsets.symmetric(horizontal: 6.0, vertical: 2.0),
    ),

    // 列表项：36 高、紧凑（规格 §2 左栏列表项）
    listTileTheme: const ListTileThemeData(
      dense: true,
      minVerticalPadding: 2.0,
      titleTextStyle: PclTypography.listItemStyle,
      subtitleTextStyle: PclTypography.captionStyle,
      iconColor: PclColors.gray3,
    ),

    // 进度条：主色 c3
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: PclColors.c3,
      linearTrackColor: PclColors.gray6,
      circularTrackColor: PclColors.gray6,
    ),

    // 滚动条：细窄的桌面观感
    scrollbarTheme: ScrollbarThemeData(
      thickness: const WidgetStatePropertyAll<double>(6.0),
      radius: const Radius.circular(PclMetrics.buttonRadius),
      thumbColor: const WidgetStatePropertyAll<Color>(PclColors.gray5),
      crossAxisMargin: 2.0,
    ),

    // 通知条：深色 = c1，白字 13
    snackBarTheme: SnackBarThemeData(
      backgroundColor: PclColors.c1,
      contentTextStyle: PclTypography.bodyStyle.copyWith(color: Colors.white),
      behavior: SnackBarBehavior.floating,
      shape: const RoundedRectangleBorder(borderRadius: panelRadius),
    ),

    // 菜单/弹层：白底 + gray5 描边
    popupMenuTheme: PopupMenuThemeData(
      color: Colors.white,
      surfaceTintColor: Colors.transparent,
      textStyle: PclTypography.bodyStyle,
      shape: const RoundedRectangleBorder(
        borderRadius: panelRadius,
        side: BorderSide(color: PclColors.gray5),
      ),
    ),
  );
}
