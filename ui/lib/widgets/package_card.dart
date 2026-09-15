/// 包卡片（下载卡片）与包源卡片。
///
/// [PackageCard] 按 `docs/DESIGN_PCL_CE.md` 第 2 节「包卡片（`MyCompItem`）」实现：
///  · 卡片总高 **64**；列布局 `7 / 50 / 8 / Auto / 1* / Auto`；行布局 `1* / 20 / 18 / 16 / 1.1*`；
///  · 左侧包源图标 **50×50**、圆角 **6**（复用 [SourceIcon]，由调用方传尺寸与圆角）；
///  · 标题 **14** / `c1` 单行省略；原始包名 **12** / `gray1` 透明度 **0.4**；
///    简介 **12** / `gray3` 单行省略；
///  · 元信息 **12**：版本 `gray3`，下载量 / 更新时间 / 许可证 `gray4`；元信息图标 **12×12**，
///    图标与文字间距 **4**；
///  · 右侧操作按钮 **25×25**、间距 **4**，**默认 Opacity 0**，只有鼠标进入整卡才变为 **1**
///    —— 这是 PCL 的真实行为：按钮位置始终占位，鼠标进入卡片才浮现；
///  · 整卡 hover 底色 `c8`、圆角 **4**。
///
/// 相比 PCL-CE 的 mod 卡片，这里换了三处内容：
///   · 封面 → 包管理器官方图标（[SourceIcon]）
///   · Mod 源 → 包管理器源（npm / pnpm / git / scoop / winget / homebrew）
///   · 下载量 → 官方优先、GitHub Stars 兜底的 [DownloadBadge]
///
/// 只有视觉与布局取自规格：数据来自 [PackageMeta]，点击行为仍由
/// `onOpen` / `onInstall` 两个既有回调承载，没有任何业务逻辑改动。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import 'pcl_widgets.dart';
import 'source_icon.dart';

// ---------------------------------------------------------------------------
// 规格里未逐项给出、但布局必需的局部数值（沿用规格的同一套量级）
// ---------------------------------------------------------------------------

/// 整卡 hover 底色的圆角 **4**（规格 §2「包卡片」）。
const double _cardRadius = 4;

/// 标题与原始包名之间的间距。
///
/// 规格给出「标题 14 / 原文名 12」但没给两者间距，取 6（同一行内两个文本块的最小呼吸量）。
const double _titleRawNameGap = 6;

/// 原始包名最多占标题行宽度的 40%（标题是主信息，不能被平分宽度提前切断）。
const double _rawNameMaxWidthRatio = 0.4;

/// 元信息项之间的间距。
///
/// 规格只给了「元信息图标与文字间距 4–5」，项与项之间取 10，
/// 让 12 号文字在 16 高的元信息行里仍然分得开。
const double _metaItemGap = 10;

/// 元信息项的弹性权重。
///
/// [Flexible] 的 flex 决定的是各项的**宽度上限**（不是固定宽度）：
/// 定长的短项（版本号 / 通道标签 / 相对时间）取 [_metaFlexShort]；
/// 下载量角标内容最长（数字 + 指标名 + 可能的"兜底"标记）取 [_metaFlexBadge]；
/// 自由长文（许可证名、已装版本）取 [_metaFlexLong]。
/// 这样窗口缩到最小宽度（810）时，最先省略的是排在**最后**的次要项，
/// 而不是把下载量数字挤成"1.…"。
const int _metaFlexShort = 1;
const int _metaFlexBadge = 3;
const int _metaFlexLong = 2;

/// 文字块与右侧操作列之间的间距（列布局 `1*` 与末列 `Auto` 之间的留白）。
///
/// 规格未给值，沿用第 3 列（图标与文字）的 8，保持同一套间距节奏。
const double _actionGap = PclMetrics.cardIconTextGap;

/// 卡片行布局 `1* / 20 / 18 / 16 / 1.1*` 用整数 flex 表示：10 : 11。
///
/// Flutter 的 flex 是整数，用 10 / 11 而不是 1 / 1.1 —— 比值一致，
/// 且上下留白（64 − 54 = 10）会被精确分成 4.76 / 5.24。
const int _cardTopFlex = 10;
const int _cardBottomFlex = 11;

/// 卡内文字的显式行高（规格的 20 / 18 / 16 三行是**固定行高**）。
///
/// 若不显式给 height，12 号字按 [PclTypography] 的 1.35 会排到 16.2，
/// 比 16 高的元信息行高出 0.2px —— 桌面端会看到最后一个像素被裁。
/// 取 1.2 后：14×1.2 = 16.8 ≤ 20，12×1.2 = 14.4 ≤ 18 / 16，三行都稳。
const double _cardLineHeight = 1.2;

/// 包卡片 —— 对应需求里的「下载卡片」。
class PackageCard extends StatelessWidget {
  const PackageCard({
    super.key,
    required this.meta,
    required this.index,
    this.onOpen,
    this.onInstall,
  });

  final PackageMeta meta;
  final int index;
  final VoidCallback? onOpen;
  final VoidCallback? onInstall;

  /// 需要显示的原始包名；与显示名相同时返回 null。
  ///
  /// 规格要求「数据模型的 name 作为原始名用 12 号 gray1 + opacity 0.4 显示」，
  /// 但当 npm 这类包源的原文名与显示名本来就是同一个字符串时，
  /// 再渲染一遍会让卡片上出现两遍同样的字（PCL 的卡片也只在两者不同时才显示原文名）。
  String? get _rawName {
    final raw = meta.name.trim();
    if (raw.isEmpty) return null;
    return raw == meta.displayName.trim() ? null : raw;
  }

  @override
  Widget build(BuildContext context) {
    final String? rawName = _rawName;

    return ListEntrance(
      index: index,
      // 「整卡 hover」需要局部状态，而 PackageCard 对外保持 StatelessWidget
      // （公开类型不变，其他页面与集成测试按类型查找），因此状态收在私有实现里。
      child: _CardHoverRegion(
        builder: (BuildContext context, bool hovered) {
          return GestureDetector(
            onTap: onOpen,
            behavior: HitTestBehavior.opaque,
            child: Container(
              // 卡片总高 64（规格 §2）
              height: PclMetrics.cardHeight,
              decoration: BoxDecoration(
                // 整卡 hover：底色 c8 + 圆角 4
                color: hovered ? PclColors.c8 : Colors.transparent,
                borderRadius: BorderRadius.circular(_cardRadius),
              ),
              child: Row(
                children: <Widget>[
                  // 列 1：左边距 7
                  const SizedBox(width: PclMetrics.cardPaddingLeft),
                  // 列 2：包源图标 50×50，圆角 6
                  SourceIcon(
                    kind: meta.source,
                    size: PclMetrics.cardIconSize,
                    radius: PclMetrics.cardIconRadius,
                  ),
                  // 列 3：图标与文字之间的间隙 8
                  const SizedBox(width: PclMetrics.cardIconTextGap),
                  // 列 4 + 5：`Auto` 内容列与 `1*` 弹性列（WPF 里是跨列的文本块，
                  // 这里用 Expanded 表示"吃满剩余宽度"，标题因此可以单行省略而不挤走按钮）
                  Expanded(child: _CardText(meta: meta, rawName: rawName)),
                  const SizedBox(width: _actionGap),
                  // 列 6：`Auto` 操作按钮列（25×25，间距 4）
                  _CardActions(
                    hovered: hovered,
                    onOpen: onOpen,
                    onInstall: onInstall,
                  ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}

/// 卡片的文字块：行布局 `1* / 20 / 18 / 16 / 1.1*`。
///
/// 第 2 行放标题（+ 原始包名）、第 3 行放简介、第 4 行放元信息；
/// 第 1 / 5 行是上下留白，用 flex 10 : 11 表示 `1* : 1.1*`。
class _CardText extends StatelessWidget {
  const _CardText({required this.meta, required this.rawName});

  final PackageMeta meta;
  final String? rawName;

  @override
  Widget build(BuildContext context) {
    final String? summary = meta.summary;
    final String? raw = rawName;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        // 行 1：1*（顶部留白）
        const Spacer(flex: _cardTopFlex),
        // 行 2：20（标题）
        SizedBox(
          height: PclMetrics.cardTitleRowHeight,
          child: LayoutBuilder(
            builder: (BuildContext context, BoxConstraints constraints) {
              // 标题是主信息、原文名是副信息。若两者都用 Flexible 平分宽度，
              // 一堆短原文名也会各占一半，把长标题提前切断；
              // 因此给原文名一个宽度上限（本行宽度的 40%），剩下的全归标题。
              final double rawMaxWidth =
                  constraints.maxWidth * _rawNameMaxWidthRatio;
              return Row(
                children: <Widget>[
                  Flexible(
                    child: _CardTextLine(
                      text: meta.displayName,
                      style: PclTypography.cardTitleStyle,
                    ),
                  ),
                  if (raw != null) ...<Widget>[
                    const SizedBox(width: _titleRawNameGap),
                    ConstrainedBox(
                      constraints: BoxConstraints(maxWidth: rawMaxWidth),
                      child: _CardTextLine(
                        text: raw,
                        // 原始名：12 号 gray1 + 透明度 0.4（token 已预乘成 ARGB，
                        // 卡片底色是页面白底，两者观感等价且少一层合成）
                        style: PclTypography.cardSubtitleStyle,
                      ),
                    ),
                  ],
                ],
              );
            },
          ),
        ),
        // 行 3：18（简介）
        SizedBox(
          height: PclMetrics.cardDescRowHeight,
          child: summary == null
              ? null
              : Align(
                  alignment: Alignment.centerLeft,
                  child: _CardTextLine(
                    text: summary,
                    style: PclTypography.captionStyle,
                  ),
                ),
        ),
        // 行 4：16（元信息）
        SizedBox(
          height: PclMetrics.cardMetaRowHeight,
          child: _CardMetaRow(meta: meta),
        ),
        // 行 5：1.1*（底部留白，略大于顶部，视觉重心略微上移）
        const Spacer(flex: _cardBottomFlex),
      ],
    );
  }
}

/// 卡片里的一行单行省略文字。
///
/// 统一走 [TextOverflow.ellipsis] + 显式 [height]，让三行的固定行高不被字体行高顶破。
class _CardTextLine extends StatelessWidget {
  const _CardTextLine({required this.text, required this.style});

  final String text;
  final TextStyle style;

  @override
  Widget build(BuildContext context) {
    return Text(
      text,
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: style.copyWith(height: _cardLineHeight),
    );
  }
}

/// 元信息行（规格 §2）：版本 / 包源通道 / 下载量 / 更新时间 / 许可证 / 已装状态。
///
/// * 每一项都用 [Flexible] 包一层：窗口被拉到最小宽度（810）时，
///   长许可证名或长版本号会自己省略，而不是让整行 [Row] 溢出；
/// * 版本用 `gray3`，其余用 `gray4`（规格原文）；
/// * 下载量继续交给 [DownloadBadge]（需求要求保留该公开组件，且它自带"兜底"标注，
///   不会把 GitHub Stars 冒充成下载量）。
class _CardMetaRow extends StatelessWidget {
  const _CardMetaRow({required this.meta});

  final PackageMeta meta;

  @override
  Widget build(BuildContext context) {
    final List<Widget> items = <Widget>[];

    // 追加一项元信息：自动补上项间间距，并按权重包一层 [Flexible]（宽度上限 + 省略号）
    void add(Widget item, {int flex = _metaFlexShort}) {
      if (items.isNotEmpty) items.add(const SizedBox(width: _metaItemGap));
      items.add(Flexible(flex: flex, child: item));
    }

    final String? version = meta.latestVersion;
    if (version != null) {
      add(_MetaBit(icon: 'tag', color: PclColors.gray3, text: version));
    }

    // 版本通道标签（正式版 / 测试版 / 快照）：与版本号同属"版本信息"
    add(ChannelChip(channel: meta.latestChannel, compact: true));
    add(DownloadBadge(stats: meta.downloads, compact: true), flex: _metaFlexBadge);

    final String? updatedAt = meta.updatedAt;
    if (updatedAt != null) {
      add(
        _MetaBit(
          icon: 'clock',
          color: PclColors.gray4,
          text: humanizeIso(updatedAt),
        ),
      );
    }

    final String? license = meta.license;
    if (license != null) {
      add(
        _MetaBit(icon: 'scroll', color: PclColors.gray4, text: license),
        flex: _metaFlexLong,
      );
    }

    // 已装状态：安装在本地时给出明确提示，可升级时换成"升级"语义的图标。
    // （这是原有卡片就有的信息，属于布局重构要保留的内容，不是新增业务逻辑。）
    final InstalledState? installed = meta.installed;
    if (installed != null) {
      add(
        _MetaBit(
          icon: installed.outdated ? 'refresh-cw' : 'circle-check',
          color: PclColors.gray4,
          text: installed.outdated
              ? '已装 ${installed.version}（可升级）'
              : '已装 ${installed.version}',
        ),
        flex: _metaFlexLong,
      );
    }

    return Row(children: items);
  }
}

/// 一条元信息：12×12 图标 + 12 号文字（图标与文字间距 4）。
class _MetaBit extends StatelessWidget {
  const _MetaBit({
    required this.icon,
    required this.color,
    required this.text,
  });

  /// lucide 图标名（见 [PclIcon]）。
  final String icon;

  /// 图标与文字共用的颜色（版本 `gray3`，其余 `gray4`）。
  final Color color;

  /// 元信息文字。
  final String text;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        PclIcon(name: icon, size: PclMetrics.metaIconSize, color: color),
        const SizedBox(width: PclMetrics.metaIconGap),
        Flexible(
          child: Text(
            text,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: TextStyle(
              fontSize: PclTypography.caption,
              color: color,
              height: _cardLineHeight,
              fontFamilyFallback: PclTypography.fontFallback,
            ),
          ),
        ),
      ],
    );
  }
}

/// 右侧操作按钮列（列 6，`Auto`）：25×25 图标按钮、间距 4、默认 Opacity 0。
///
/// PCL 的卡片按钮平时是"隐身"的，鼠标进入整卡才浮现（规格 §2：「默认 Opacity 0（hover 显示）」）。
/// 这里用 [Opacity] 而不是 `Visibility`，按钮的位置始终占位 ——
/// 否则 hover 时文字会被突然挤窄，产生跳动。
class _CardActions extends StatelessWidget {
  const _CardActions({
    required this.hovered,
    this.onOpen,
    this.onInstall,
  });

  /// 整卡是否处于 hover 状态。
  final bool hovered;

  /// 「查看版本」回调（原 onOpen）。
  final VoidCallback? onOpen;

  /// 「安装」回调（原 onInstall）。
  final VoidCallback? onInstall;

  @override
  Widget build(BuildContext context) {
    final VoidCallback? install = onInstall;
    final VoidCallback? open = onOpen;

    return Opacity(
      opacity: hovered ? 1 : PclMetrics.cardActionIdleOpacity,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          if (install != null)
            PclIconButton(
              icon: 'download',
              onPressed: install,
              size: PclMetrics.cardActionButtonSize,
              tooltip: '安装',
            ),
          if (install != null && open != null)
            const SizedBox(width: PclMetrics.cardActionSpacing),
          if (open != null)
            PclIconButton(
              icon: 'external-link',
              onPressed: open,
              size: PclMetrics.cardActionButtonSize,
              tooltip: '查看版本',
            ),
        ],
      ),
    );
  }
}

/// 卡片整卡悬停侦测器：把"是否 hover"这一局部状态收敛在私有 [State] 里，
/// 让公开的 [PackageCard] 保持契约声明的 `StatelessWidget`。
/// 做法与 `pcl_widgets.dart` 内部的 `_HoverRegion` 一致。
class _CardHoverRegion extends StatefulWidget {
  const _CardHoverRegion({required this.builder});

  /// 依据 hover 状态构建子树。
  final Widget Function(BuildContext context, bool hovered) builder;

  @override
  State<_CardHoverRegion> createState() => _CardHoverRegionState();
}

class _CardHoverRegionState extends State<_CardHoverRegion> {
  bool _hovered = false;

  void _setHovered(bool value) {
    if (_hovered == value) return;
    setState(() => _hovered = value);
  }

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => _setHovered(true),
      onExit: (_) => _setHovered(false),
      child: widget.builder(context, _hovered),
    );
  }
}

/// 包源卡片 —— 包源列表里的一张卡。
///
/// 平台不支持的包源会以"灰显 + 说明"的形式出现（当用户打开"显示不支持的包源"时），
/// 这让"平台能力检测"这一行为是**可见、可验证**的，而不是悄悄消失。
///
/// 视觉沿用 [PclMetrics.windowRadius]（6）与 PCL 颜色 token，
/// **判定逻辑（`isSelectable` / 可用性状态文案）一个字都没动**。
class SourceCard extends StatelessWidget {
  const SourceCard({
    super.key,
    required this.source,
    required this.index,
    required this.selected,
    this.onTap,
  });

  final SourceView source;
  final int index;
  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final disabled = !source.isSelectable;
    final highlighted = selected && !disabled;

    final statusText = switch (source.availability.state) {
      AvailabilityState.available => source.availability.cliVersion ?? '可用',
      AvailabilityState.degraded => '数据可能过期',
      AvailabilityState.unsupportedPlatform => '当前平台不支持',
      AvailabilityState.notInstalled => '未安装',
      AvailabilityState.unreachable => '源不可达',
    };

    final statusColor = switch (source.availability.state) {
      AvailabilityState.available => PclColors.c2,
      AvailabilityState.degraded => const Color(0xFF8A5300),
      _ => PclColors.error,
    };

    return ListEntrance(
      index: index,
      child: Opacity(
        opacity: disabled ? 0.55 : 1,
        child: Material(
          color: highlighted ? PclColors.c6 : Colors.transparent,
          borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
          child: InkWell(
            onTap: disabled ? null : onTap,
            borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(PclMetrics.windowRadius),
                border: Border.all(
                  color: highlighted ? PclColors.c3 : PclColors.gray6,
                  width: highlighted ? 1.5 : PclMetrics.buttonBorderWidth,
                ),
              ),
              child: Row(
                children: <Widget>[
                  SourceIcon(
                    kind: source.kind,
                    size: 34,
                    accentHex: source.accentHex,
                    radius: PclMetrics.cardIconRadius,
                  ),
                  const SizedBox(width: 11),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: <Widget>[
                        Row(
                          children: <Widget>[
                            Text(
                              source.displayName,
                              style: PclTypography.bodyStyle.copyWith(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                            if (source.verificationNote != null) ...<Widget>[
                              const SizedBox(width: 6),
                              Tooltip(
                                message: source.verificationNote!,
                                child: const Icon(
                                  Icons.science_outlined,
                                  size: 13,
                                  color: Color(0xFF8A5300),
                                ),
                              ),
                            ],
                          ],
                        ),
                        const SizedBox(height: 3),
                        Text(
                          '$statusText · 默认作用域：${source.defaultScope.displayName}',
                          style: TextStyle(
                            fontSize: PclTypography.label,
                            color: statusColor,
                            fontFamilyFallback: PclTypography.fontFallback,
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ],
                    ),
                  ),
                  if (disabled)
                    Icon(Icons.block, size: 16, color: PclColors.gray3),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// 把 ISO-8601 时间转成"3 天前"这类可读文案。
/// 与 Rust 侧 `clock::humanize_age` 的行为保持一致。
String humanizeIso(String iso) {
  final parsed = DateTime.tryParse(iso);
  if (parsed == null) return iso;
  final diff = DateTime.now().toUtc().difference(parsed.toUtc());
  if (diff.isNegative) return '刚刚';
  if (diff.inSeconds < 60) return '${diff.inSeconds} 秒前';
  if (diff.inMinutes < 60) return '${diff.inMinutes} 分钟前';
  if (diff.inHours < 24) return '${diff.inHours} 小时前';
  if (diff.inDays < 30) return '${diff.inDays} 天前';
  if (diff.inDays < 365) return '${(diff.inDays / 30).floor()} 个月前';
  return '${(diff.inDays / 365).floor()} 年前';
}

/// 版本号颜色随通道变化，便于列表页快速扫描。
Color channelColor(ReleaseChannel channel, BuildContext context) => switch (channel) {
      ReleaseChannel.stable => const Color(0xFF1E6B33),
      ReleaseChannel.prerelease => const Color(0xFF8A5300),
      ReleaseChannel.snapshot => const Color(0xFF4A3B8F),
      ReleaseChannel.unknown => Theme.of(context).colorScheme.onSurfaceVariant,
    };
