/// 包卡片（下载卡片）与包源卡片。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../models/umango_models.dart';
import '../theme/app_theme.dart';
import 'source_icon.dart';

/// 包卡片 —— 对应需求里的「下载卡片」。
///
/// 相比 PCL-CE 的 mod 卡片，这里换了三处内容：
///   · 封面 → 包管理器官方图标（[SourceIcon]）
///   · Mod 源 → 包管理器源（npm / pnpm / git / scoop / winget / homebrew）
///   · 下载量 → 官方优先、GitHub Stars 兜底的[DownloadBadge]
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

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ListEntrance(
      index: index,
      child: Card(
        child: InkWell(
          onTap: onOpen,
          borderRadius: BorderRadius.circular(10),
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SourceIcon(kind: meta.source, size: 44),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              meta.displayName,
                              style: theme.textTheme.titleSmall?.copyWith(
                                fontWeight: FontWeight.w700,
                                fontSize: 14.5,
                              ),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          const SizedBox(width: 8),
                          if (meta.latestVersion != null) ...[
                            Text(
                              meta.latestVersion!,
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: theme.colorScheme.primary,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                            const SizedBox(width: 6),
                            ChannelChip(channel: meta.latestChannel, compact: true),
                          ],
                        ],
                      ),
                      if (meta.summary != null) ...[
                        const SizedBox(height: 6),
                        Text(
                          meta.summary!,
                          style: theme.textTheme.bodySmall
                              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ],
                      const SizedBox(height: 10),
                      Wrap(
                        spacing: 14,
                        runSpacing: 6,
                        crossAxisAlignment: WrapCrossAlignment.center,
                        children: [
                          DownloadBadge(stats: meta.downloads, compact: true),
                          if (meta.license != null)
                            _MetaBit(icon: Icons.gavel_outlined, text: meta.license!),
                          if (meta.updatedAt != null)
                            _MetaBit(
                              icon: Icons.schedule_outlined,
                              text: humanizeIso(meta.updatedAt!),
                            ),
                          if (meta.installed != null)
                            _MetaBit(
                              icon: meta.installed!.outdated
                                  ? Icons.upgrade_outlined
                                  : Icons.check_circle_outline,
                              text: meta.installed!.outdated
                                  ? '已装 ${meta.installed!.version}（可升级）'
                                  : '已装 ${meta.installed!.version}',
                            ),
                        ],
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                Column(
                  children: [
                    OutlinedButton(
                      onPressed: onOpen,
                      child: const Text('查看版本'),
                    ),
                    if (onInstall != null) ...[
                      const SizedBox(height: 6),
                      TextButton.icon(
                        onPressed: onInstall,
                        icon: const Icon(Icons.download_outlined, size: 16),
                        label: const Text('安装'),
                      ),
                    ],
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _MetaBit extends StatelessWidget {
  const _MetaBit({required this.icon, required this.text});

  final IconData icon;
  final String text;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 13, color: theme.colorScheme.outline),
        const SizedBox(width: 4),
        Text(text, style: TextStyle(fontSize: 11.5, color: theme.colorScheme.outline)),
      ],
    );
  }
}

/// 包源卡片 —— 包源列表里的一张卡。
///
/// 平台不支持的包源会以"灰显 + 说明"的形式出现（当用户打开"显示不支持的包源"时），
/// 这让"平台能力检测"这一行为是**可见、可验证**的，而不是悄悄消失。
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
    final theme = Theme.of(context);
    final accent = AppTheme.hexColor(source.accentHex);
    final disabled = !source.isSelectable;

    final statusText = switch (source.availability.state) {
      AvailabilityState.available => source.availability.cliVersion ?? '可用',
      AvailabilityState.degraded => '数据可能过期',
      AvailabilityState.unsupportedPlatform => '当前平台不支持',
      AvailabilityState.notInstalled => '未安装',
      AvailabilityState.unreachable => '源不可达',
    };

    final statusColor = switch (source.availability.state) {
      AvailabilityState.available => const Color(0xFF1E6B33),
      AvailabilityState.degraded => const Color(0xFF8A5300),
      _ => theme.colorScheme.error,
    };

    return ListEntrance(
      index: index,
      child: Opacity(
        opacity: disabled ? 0.55 : 1,
        child: Material(
          color: selected && !disabled
              ? theme.colorScheme.primaryContainer.withValues(alpha: 0.45)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(10),
          child: InkWell(
            onTap: disabled ? null : onTap,
            borderRadius: BorderRadius.circular(10),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(10),
                border: Border.all(
                  color: selected && !disabled
                      ? theme.colorScheme.primary
                      : theme.dividerColor,
                  width: selected && !disabled ? 1.5 : 1,
                ),
              ),
              child: Row(
                children: [
                  SourceIcon(kind: source.kind, size: 34, accentHex: source.accentHex),
                  const SizedBox(width: 11),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            Text(
                              source.displayName,
                              style: theme.textTheme.bodyMedium
                                  ?.copyWith(fontWeight: FontWeight.w700),
                            ),
                            if (source.verificationNote != null) ...[
                              const SizedBox(width: 6),
                              Tooltip(
                                message: source.verificationNote!,
                                child: const Icon(Icons.science_outlined,
                                    size: 13, color: Color(0xFF8A5300)),
                              ),
                            ],
                          ],
                        ),
                        const SizedBox(height: 3),
                        Text(
                          '$statusText · 默认作用域：${source.defaultScope.displayName}',
                          style: TextStyle(fontSize: 11, color: statusColor),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ],
                    ),
                  ),
                  if (disabled)
                    Icon(Icons.block, size: 16, color: theme.colorScheme.outline),
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
