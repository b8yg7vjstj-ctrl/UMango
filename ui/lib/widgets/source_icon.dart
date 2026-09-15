/// 包源图标（下载卡片封面）与两个卡片角标（[ChannelChip] / [DownloadBadge]）。
///
/// 需求：卡片封面不再使用 mod 封面，改为对应工具的官方图标
/// （Homebrew、npm、git、scoop、winget）。
///
/// 实际情况与处理方式（如实说明）：
///   · npm / pnpm / git / homebrew：使用 **simple-icons 的 CC0 官方图标**，
///     以品牌主色着色渲染；
///   · winget / scoop：simple-icons 里没有这两个品牌图标，开发环境也无法访问
///     github.com 获取官方资源，因此使用**自绘字母徽章**（品牌主色圆角方块 + 字母）。
///     宁可样式朴素，也不要放来源不明或画错的商标图形。
///
/// 想换成官方 logo 时：把 `winget.svg` / `scoop.svg` 放进 `assets/icons/`，
/// 在 [_svgAssets] 里登记即可，其他代码无需改动。
///
/// 视觉对齐全 `docs/DESIGN_PCL_CE.md`：
///   · [SourceIcon] 的圆角默认 **6**（对应 PCL 卡片图标的 6px 圆角，规格 §2）；
///   · [ChannelChip] / [DownloadBadge] 按 [PclTag] 观感 —— 圆角 **3**、字号 **11**。
library;

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

import '../models/umango_models.dart';
import '../theme/app_theme.dart';
import '../theme/pcl_theme.dart';
import 'pcl_widgets.dart';

/// 有 CC0 官方 SVG 图标的包源。
const Map<PackageSourceKind, String> _svgAssets = {
  PackageSourceKind.npm: 'assets/icons/npm.svg',
  PackageSourceKind.pnpm: 'assets/icons/pnpm.svg',
  PackageSourceKind.git: 'assets/icons/git.svg',
  PackageSourceKind.homebrew: 'assets/icons/homebrew.svg',
};

/// 没有官方图标时使用的字母徽章文字。
const Map<PackageSourceKind, String> _monograms = {
  PackageSourceKind.winget: 'W',
  PackageSourceKind.scoop: 'S',
};

class SourceIcon extends StatelessWidget {
  const SourceIcon({
    super.key,
    required this.kind,
    this.size = 40,
    this.accentHex,
    this.showBackground = true,
    this.radius = PclMetrics.cardIconRadius,
  });

  final PackageSourceKind kind;
  final double size;

  /// 覆盖强调色（来自核心层的 accent 字段）
  final String? accentHex;

  /// 是否画底色方块。false 时只输出图形本身。
  final bool showBackground;

  /// 底色方块圆角，默认 [PclMetrics.cardIconRadius]（**6**）。
  ///
  /// 规格 §2「包卡片」给的是 50×50 / 圆角 6 —— 卡片内由调用方传
  /// `size: PclMetrics.cardIconSize, radius: PclMetrics.cardIconRadius`。
  /// 这里默认给同一个 6 而不是按 size 比例算，是为了让"大小不同、圆角一致"
  /// 在 PCL 的列表里看起来是一套东西（旧实现用 size×0.26，尺寸一变圆角就跟着漂）。
  final double radius;

  @override
  Widget build(BuildContext context) {
    final accent = AppTheme.hexColor(accentHex ?? kind.accentHex);
    final asset = _svgAssets[kind];

    final Widget glyph = asset != null
        // simple-icons 是单色路径图，用 srcIn 着色成品牌主色
        ? SvgPicture.asset(
            asset,
            width: size * 0.62,
            height: size * 0.62,
            colorFilter: ColorFilter.mode(
              showBackground ? Colors.white : accent,
              BlendMode.srcIn,
            ),
          )
        : Text(
            _monograms[kind] ?? kind.displayName.characters.first,
            style: TextStyle(
              color: showBackground ? Colors.white : accent,
              fontSize: size * 0.46,
              fontWeight: FontWeight.w700,
              height: 1,
            ),
          );

    if (!showBackground) {
      return SizedBox(width: size, height: size, child: Center(child: glyph));
    }

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        // 用品牌主色的渐变，让卡片封面有辨识度又不喧宾夺主
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [accent, Color.lerp(accent, Colors.black, 0.22) ?? accent],
        ),
        borderRadius: BorderRadius.circular(radius),
      ),
      child: Center(child: glyph),
    );
  }
}

/// 版本通道标签（正式版 / 测试版 / 快照 / 未知）。
///
/// 外观按 [PclTag]：圆角 [PclMetrics.tagRadius]（**3**）、字号
/// [PclTypography.label]（**11**）；底色仍按通道取语义色（与
/// `channelColor()` 的版本号配色一致），而不是一律用 PclTag 的 `c6` ——
/// 通道色是这个标签唯一的信息量。
class ChannelChip extends StatelessWidget {
  const ChannelChip({super.key, required this.channel, this.compact = false});

  final ReleaseChannel channel;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final (bg, fg) = switch (channel) {
      ReleaseChannel.stable => (const Color(0xFFE6F4EA), const Color(0xFF1E6B33)),
      ReleaseChannel.prerelease => (const Color(0xFFFFF3E0), const Color(0xFF8A5300)),
      ReleaseChannel.snapshot => (const Color(0xFFEDE7F6), const Color(0xFF4A3B8F)),
      ReleaseChannel.unknown => (const Color(0xFFEDEFF2), const Color(0xFF5B6470)),
    };

    return Container(
      // 垂直内边距：紧凑模式 1 —— 11 号字（13.2 高）+ 上下各 1 = 15.2，
      // 正好放进卡片元信息行的 16 高，不会被行高裁掉；
      // 非紧凑模式（详情页）保留 PclTag 的 2。
      padding: EdgeInsets.symmetric(
        horizontal: compact ? 6 : 8,
        vertical: compact ? 1 : 2,
      ),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(PclMetrics.tagRadius),
      ),
      child: Text(
        channel.displayName,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: TextStyle(
          color: fg,
          fontSize: PclTypography.label,
          fontWeight: FontWeight.w600,
          height: 1.2,
          fontFamilyFallback: PclTypography.fontFallback,
        ),
      ),
    );
  }
}

/// 下载量角标。
///
/// 关键点：**区分"下载量"与"GitHub Stars"**。
/// 兜底数据必须显式标注，否则用户会把 Star 数误读成下载量。
///
/// 视觉按规格 §2「包卡片」的元信息项：图标 **12×12**（[PclMetrics.metaIconSize]）、
/// 图标与文字间距 **4**、数字 **12** 号、指标名 **11** 号（[PclTypography.label]）；
/// 紧凑模式（卡片元信息行）数字用 `gray4`，非紧凑模式（详情页）用 `c1` 强调。
/// 整个角标可以被压窄（内部文字走 [Flexible] + 省略号），
/// 因此在只有 16 高的元信息行里也不会把整行挤溢出。
class DownloadBadge extends StatelessWidget {
  const DownloadBadge({super.key, required this.stats, this.compact = false});

  final DownloadStats stats;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final count = stats.count;
    final isUnknown = count == null;

    // lucide 图标：下载量 / Stars / 未知（stars 与未知必须一眼可分）
    final iconName = switch (stats) {
      OfficialDownloadStats() => 'download',
      GitHubStarsStats() => 'star',
      UnknownDownloadStats() => 'help-circle',
    };

    // 紧凑模式下的短指标名。
    final shortMetric = switch (stats) {
      OfficialDownloadStats() => '下载量',
      GitHubStarsStats() => 'Stars',
      UnknownDownloadStats() => '',
    };

    // 元信息行里的数字是 gray4（规格 §2），详情页用 c1 让数值更醒目
    final valueColor = isUnknown
        ? PclColors.gray3
        : (compact ? PclColors.gray4 : PclColors.c1);

    return Tooltip(
      message: '${stats.metricName}·${stats.sourceLabel}'
          '${stats.isFallback ? '\n（官方下载量不可用，已按规则兜底为 GitHub Stars）' : ''}',
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          PclIcon(
            name: iconName,
            size: compact ? PclMetrics.metaIconSize : 14,
            color: valueColor,
          ),
          const SizedBox(width: PclMetrics.metaIconGap),
          Flexible(
            child: Text(
              isUnknown ? '暂无数据' : formatCountCompact(count),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: PclTypography.caption,
                fontWeight: FontWeight.w600,
                color: valueColor,
                height: 1.2,
                fontFamilyFallback: PclTypography.fontFallback,
              ),
            ),
          ),
          // 指标名：非紧凑模式总是显示；紧凑模式下仅兜底时显示（避免误读）
          if (!isUnknown && (!compact || stats.isFallback)) ...<Widget>[
            const SizedBox(width: 4),
            Flexible(
              child: Text(
                compact ? shortMetric : stats.metricName,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontSize: PclTypography.label,
                  color: PclColors.gray3,
                  height: 1.2,
                  fontFamilyFallback: PclTypography.fontFallback,
                ),
              ),
            ),
          ],
          if (stats.isFallback) ...<Widget>[
            const SizedBox(width: 4),
            // 「兜底」标记：**紧凑模式也必须显示** ——
            // 早期版本只在非紧凑模式标注，害得下载卡片上的 GitHub Stars
            // 看起来就是下载量（这个缺陷是被集成测试抓出来的，不要退化）。
            _FallbackTag(compact: compact),
          ],
        ],
      ),
    );
  }
}

/// 「兜底」小标记：暖色底 + 深棕字，圆角 3、字号 11（PclTag 观感）。
class _FallbackTag extends StatelessWidget {
  const _FallbackTag({required this.compact});

  /// 紧凑模式（卡片元信息行）：垂直内边距收到 1，以便塞进 16 高的行。
  final bool compact;

  /// 兜底标记的底色 / 文字色。
  ///
  /// 用暖色而不是 PclTag 的 `c6`/`c2`：这个标记要传达"这不是下载量"的警告语义，
  /// 与蓝色的普通标签必须能一眼区分，否则标注等于没标。
  static const Color _background = Color(0xFFFFF3E0);
  static const Color _foreground = Color(0xFF8A5300);

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: compact ? 4 : 6,
        vertical: compact ? 1 : 2,
      ),
      decoration: BoxDecoration(
        color: _background,
        borderRadius: BorderRadius.circular(PclMetrics.tagRadius),
      ),
      child: Text(
        '兜底',
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: const TextStyle(
          fontSize: PclTypography.label,
          color: _foreground,
          height: 1.2,
          fontFamilyFallback: PclTypography.fontFallback,
        ),
      ),
    );
  }
}
