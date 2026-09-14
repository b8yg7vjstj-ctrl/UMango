/// 包源图标（下载卡片封面）。
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
library;

import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';

import '../models/umango_models.dart';
import '../theme/app_theme.dart';

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
  });

  final PackageSourceKind kind;
  final double size;

  /// 覆盖强调色（来自核心层的 accent 字段）
  final String? accentHex;

  /// 是否画底色方块。false 时只输出图形本身。
  final bool showBackground;

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
        borderRadius: BorderRadius.circular(size * 0.26),
      ),
      child: Center(child: glyph),
    );
  }
}

/// 版本通道标签（正式版 / 测试版 / 快照 / 未知）
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
      padding: EdgeInsets.symmetric(horizontal: compact ? 6 : 8, vertical: compact ? 2 : 3),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(5)),
      child: Text(
        channel.displayName,
        style: TextStyle(
          color: fg,
          fontSize: compact ? 11 : 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

/// 下载量角标。
///
/// 关键点：**区分"下载量"与"GitHub Stars"**。
/// 兜底数据必须显式标注，否则用户会把 Star 数误读成下载量。
class DownloadBadge extends StatelessWidget {
  const DownloadBadge({super.key, required this.stats, this.compact = false});

  final DownloadStats stats;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final count = stats.count;
    final isUnknown = count == null;

    final icon = switch (stats) {
      OfficialDownloadStats() => Icons.download_outlined,
      GitHubStarsStats() => Icons.star_outline,
      UnknownDownloadStats() => Icons.help_outline,
    };

    return Tooltip(
      message: '${stats.metricName}·${stats.sourceLabel}'
          '${stats.isFallback ? '\n（官方下载量不可用，已按规则兜底为 GitHub Stars）' : ''}',
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon,
              size: compact ? 13 : 15,
              color: isUnknown ? theme.colorScheme.outline : theme.colorScheme.primary),
          const SizedBox(width: 4),
          Text(
            isUnknown ? '暂无数据' : formatCountCompact(count),
            style: TextStyle(
              fontSize: compact ? 11 : 12.5,
              fontWeight: FontWeight.w600,
              color: isUnknown ? theme.colorScheme.outline : theme.colorScheme.onSurface,
            ),
          ),
          if (!compact && !isUnknown) ...[
            const SizedBox(width: 3),
            Text(
              stats.metricName,
              style: TextStyle(fontSize: 11, color: theme.colorScheme.outline),
            ),
          ],
          if (stats.isFallback && !compact) ...[
            const SizedBox(width: 4),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
              decoration: BoxDecoration(
                color: const Color(0xFFFFF3E0),
                borderRadius: BorderRadius.circular(4),
              ),
              child: const Text('兜底',
                  style: TextStyle(fontSize: 10, color: Color(0xFF8A5300))),
            ),
          ],
        ],
      ),
    );
  }
}
