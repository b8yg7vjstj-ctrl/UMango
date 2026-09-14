/// 依赖关系树可视化。
///
/// 需求：
///   · 版本列表依赖关系树可视化，**清晰展示依赖链条**；
///   · 依赖项可单独点击，支持「直接安装依赖」或「跳转至依赖版本详情页」；
///   · 依赖故障要有兜底分支（网络异常、源访问失败时的呈现）。
///
/// 呈现方式：缩进 + 连接线（而不是紧凑的横排图）——
/// 包依赖的深度通常 2~4 层、每层节点数不定，纵向缩进树在桌面宽屏上可读性最好，
/// 也天然适配"列表逐条入场"的动画方向。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../models/umango_models.dart';
import '../theme/app_theme.dart';
import 'source_icon.dart';

/// 依赖树面板。
class DependencyTreeView extends StatefulWidget {
  const DependencyTreeView({
    super.key,
    required this.tree,
    this.onInstallDependency,
    this.onOpenDependency,
    this.maxVisibleNodes = 400,
  });

  final DependencyTree tree;

  /// 点击「安装」：直接安装该依赖
  final void Function(DependencyNode node)? onInstallDependency;

  /// 点击节点：跳转到该依赖的版本详情页
  final void Function(DependencyNode node)? onOpenDependency;

  /// 超过这个数量就提示截断，避免超长列表把界面拖垮
  final int maxVisibleNodes;

  @override
  State<DependencyTreeView> createState() => _DependencyTreeViewState();
}

class _DependencyTreeViewState extends State<DependencyTreeView> {
  /// 折叠起来的节点 id 集合
  final Set<String> _collapsed = {};

  @override
  Widget build(BuildContext context) {
    final flat = <_FlatNode>[];
    void walk(DependencyNode node, bool isLast, List<bool> ancestorsHasMore) {
      flat.add(_FlatNode(node, isLast, List.of(ancestorsHasMore)));
      final collapsed = _collapsed.contains(_nodeKey(node));
      if (collapsed) return;
      for (var i = 0; i < node.children.length; i++) {
        final child = node.children[i];
        final childIsLast = i == node.children.length - 1;
        walk(child, childIsLast, [...ancestorsHasMore, !isLast]);
      }
    }

    walk(widget.tree.root, true, const []);

    final visible = flat.take(widget.maxVisibleNodes).toList();
    final truncatedByUi = flat.length > widget.maxVisibleNodes;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _TreeSummary(tree: widget.tree, nodeCount: flat.length),
        if (widget.tree.warnings.isNotEmpty) ...[
          const SizedBox(height: 12),
          _WarningsPanel(warnings: widget.tree.warnings),
        ],
        const SizedBox(height: 12),
        ...visible.asMap().entries.map((entry) {
          final item = entry.value;
          return DependencyRow(
            key: ValueKey(_nodeKey(item.node) + item.ancestorsHasMore.length.toString()),
            node: item.node,
            index: entry.key,
            isLast: item.isLast,
            ancestorsHasMore: item.ancestorsHasMore,
            collapsed: _collapsed.contains(_nodeKey(item.node)),
            hasChildren: item.node.children.isNotEmpty,
            onToggle: () => setState(() {
              final key = _nodeKey(item.node);
              if (!_collapsed.remove(key)) _collapsed.add(key);
            }),
            onInstall: widget.onInstallDependency == null
                ? null
                : () => widget.onInstallDependency!(item.node),
            onOpen: widget.onOpenDependency == null
                ? null
                : () => widget.onOpenDependency!(item.node),
          );
        }),
        if (truncatedByUi || widget.tree.truncated)
          Padding(
            padding: const EdgeInsets.only(top: 10),
            child: Text(
              widget.tree.truncated
                  ? '依赖树已达到深度或节点上限，更深层未展开（这是刻意的保护，避免依赖爆炸拖垮界面）'
                  : '为保证流畅，仅显示前 ${widget.maxVisibleNodes} 个节点',
              style: TextStyle(
                fontSize: 11.5,
                color: Theme.of(context).colorScheme.outline,
              ),
            ),
          ),
      ],
    );
  }

  /// 用「父路径 + 名字」做 key：同名依赖在不同分支下应能独立折叠
  String _nodeKey(DependencyNode node) => '${node.depth}:${node.name}';
}

class _FlatNode {
  _FlatNode(this.node, this.isLast, this.ancestorsHasMore);
  final DependencyNode node;
  final bool isLast;
  final List<bool> ancestorsHasMore;
}

/// 依赖树的统计头部：一眼看出总共多少个、有没有问题。
class _TreeSummary extends StatelessWidget {
  const _TreeSummary({required this.tree, required this.nodeCount});

  final DependencyTree tree;
  final int nodeCount;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final s = tree.summary;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest.withValues(alpha: 0.5),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Wrap(
        spacing: 10,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text('共 $nodeCount 个依赖节点',
              style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600)),
          if (s.maxDepth > 0)
            _Stat(icon: Icons.account_tree_outlined, text: '最深 ${s.maxDepth} 层', tone: null),
          if (s.unverified > 0)
            _Stat(
              icon: Icons.help_outline,
              text: '${s.unverified} 个未验证',
              tone: const Color(0xFF8A5300),
            ),
          if (s.missing > 0)
            _Stat(
              icon: Icons.error_outline,
              text: '${s.missing} 个找不到匹配版本',
              tone: theme.colorScheme.error,
            ),
          if (s.conflict > 0)
            _Stat(
              icon: Icons.warning_amber_outlined,
              text: '${s.conflict} 个版本冲突',
              tone: theme.colorScheme.error,
            ),
          if (s.cyclic > 0)
            _Stat(
              icon: Icons.loop,
              text: '${s.cyclic} 个依赖成环（已截断）',
              tone: const Color(0xFF4A3B8F),
            ),
          if (!s.hasProblems && s.unverified == 0)
            const _Stat(
              icon: Icons.check_circle_outline,
              text: '依赖全部解析成功',
              tone: Color(0xFF1E6B33),
            ),
        ],
      ),
    );
  }
}

class _Stat extends StatelessWidget {
  const _Stat({required this.icon, required this.text, required this.tone});

  final IconData icon;
  final String text;
  final Color? tone;

  @override
  Widget build(BuildContext context) {
    final color = tone ?? Theme.of(context).colorScheme.onSurfaceVariant;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: color),
        const SizedBox(width: 4),
        Text(text, style: TextStyle(fontSize: 12, color: color)),
      ],
    );
  }
}

/// 依赖故障的兜底呈现：把降级原因明确列出来，而不是静默显示一棵残缺的树。
class _WarningsPanel extends StatelessWidget {
  const _WarningsPanel({required this.warnings});

  final List<String> warnings;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ListEntrance(
      index: 0,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: const Color(0xFFFFF8E1),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: const Color(0xFFFFE0A3)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.info_outline, size: 15, color: Color(0xFF8A5300)),
                const SizedBox(width: 6),
                Text(
                  '解析过程中有 ${warnings.length} 项降级处理（网络异常或源访问失败的兜底）',
                  style: theme.textTheme.bodySmall?.copyWith(
                    fontWeight: FontWeight.w600,
                    color: const Color(0xFF8A5300),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            ...warnings.take(6).map(
                  (w) => Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: SelectableText(
                      '· $w',
                      style: const TextStyle(fontSize: 11.5, color: Color(0xFF6B4200)),
                    ),
                  ),
                ),
            if (warnings.length > 6)
              Text('… 其余 ${warnings.length - 6} 项省略',
                  style: const TextStyle(fontSize: 11, color: Color(0xFF8A5300))),
          ],
        ),
      ),
    );
  }
}

/// 单行依赖节点：缩进 + 连接线 + 状态 + 可点操作。
class DependencyRow extends StatelessWidget {
  const DependencyRow({
    super.key,
    required this.node,
    required this.index,
    required this.isLast,
    required this.ancestorsHasMore,
    required this.collapsed,
    required this.hasChildren,
    this.onToggle,
    this.onInstall,
    this.onOpen,
  });

  final DependencyNode node;
  final int index;
  final bool isLast;
  final List<bool> ancestorsHasMore;
  final bool collapsed;
  final bool hasChildren;
  final VoidCallback? onToggle;
  final VoidCallback? onInstall;
  final VoidCallback? onOpen;

  static const double _indentWidth = 20;
  static const double _rowHeight = 46;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final (stateColor, stateIcon, stateText) = _stateVisual(node, theme);

    return ListEntrance(
      index: index,
      child: SizedBox(
        height: _rowHeight,
        child: Row(
          children: [
            // 缩进与连接线
            if (node.depth > 0) ...[
              SizedBox(
                width: _indentWidth * node.depth,
                child: CustomPaint(
                  painter: _TreeLinesPainter(
                    depth: node.depth,
                    isLast: isLast,
                    ancestorsHasMore: ancestorsHasMore,
                    color: theme.dividerColor,
                  ),
                ),
              ),
            ],
            // 折叠箭头
            SizedBox(
              width: 20,
              child: hasChildren
                  ? InkWell(
                      onTap: onToggle,
                      borderRadius: BorderRadius.circular(4),
                      child: Icon(
                        collapsed ? Icons.chevron_right : Icons.expand_more,
                        size: 18,
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    )
                  : const SizedBox.shrink(),
            ),
            const SizedBox(width: 4),
            // 状态圆点
            Tooltip(
              message: stateText,
              child: Icon(stateIcon, size: 15, color: stateColor),
            ),
            const SizedBox(width: 8),
            // 名字（可点击跳转详情页）
            Flexible(
              child: InkWell(
                onTap: onOpen,
                borderRadius: BorderRadius.circular(4),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 4, horizontal: 2),
                  child: Text(
                    node.name,
                    style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w600,
                      decoration: onOpen != null ? TextDecoration.underline : null,
                      decorationColor: theme.colorScheme.outline,
                    ),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              ),
            ),
            const SizedBox(width: 8),
            // 解析出的版本 / 声明的范围
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest,
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                node.displayVersion,
                style: TextStyle(
                  fontSize: 11.5,
                  fontFamily: 'monospace',
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ),
            const SizedBox(width: 8),
            if (node.kind != DependencyKind.runtime)
              Padding(
                padding: const EdgeInsets.only(right: 6),
                child: Text(
                  node.kind.displayName,
                  style: TextStyle(fontSize: 10.5, color: theme.colorScheme.outline),
                ),
              ),
            const Spacer(),
            // 依赖自身的指标
            DownloadBadge(stats: node.downloads, compact: true),
            const SizedBox(width: 8),
            // 两种操作：直接安装 / 跳转详情
            if (onInstall != null)
              Tooltip(
                message: '直接安装该依赖',
                child: IconButton(
                  onPressed: onInstall,
                  iconSize: 16,
                  visualDensity: VisualDensity.compact,
                  icon: const Icon(Icons.download_outlined),
                ),
              ),
            if (onOpen != null)
              Tooltip(
                message: '跳转到该依赖的版本详情',
                child: IconButton(
                  onPressed: onOpen,
                  iconSize: 16,
                  visualDensity: VisualDensity.compact,
                  icon: const Icon(Icons.arrow_forward_outlined),
                ),
              ),
          ],
        ),
      ),
    );
  }

  (Color, IconData, String) _stateVisual(DependencyNode node, ThemeData theme) {
    return switch (node.state) {
      DependencyState.resolved => (
          const Color(0xFF1E6B33),
          Icons.check_circle_outline,
          '版本已解析',
        ),
      DependencyState.unverified => (
          const Color(0xFF8A5300),
          Icons.help_outline,
          '未验证：${node.reason ?? '无法获取该依赖的元数据'}',
        ),
      DependencyState.missing => (
          theme.colorScheme.error,
          Icons.error_outline,
          '没有满足 ${node.requirement} 的版本',
        ),
      DependencyState.conflict => (
          theme.colorScheme.error,
          Icons.warning_amber_outlined,
          '版本冲突：${node.reason ?? ''}',
        ),
      DependencyState.cyclic => (
          const Color(0xFF4A3B8F),
          Icons.loop,
          '依赖成环，已在此截断：${node.reason ?? ''}',
        ),
      DependencyState.truncated => (
          theme.colorScheme.outline,
          Icons.more_horiz,
          '未展开（达到深度上限）',
        ),
    };
  }
}

/// 画缩进用的竖线与拐角。用线条呈现层级，比纯缩进更能看清"链条"。
class _TreeLinesPainter extends CustomPainter {
  _TreeLinesPainter({
    required this.depth,
    required this.isLast,
    required this.ancestorsHasMore,
    required this.color,
  });

  final int depth;
  final bool isLast;
  final List<bool> ancestorsHasMore;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..strokeWidth = 1
      ..style = PaintingStyle.stroke;

    const step = DependencyRow._indentWidth;
    final midY = size.height / 2;

    // 每一层祖先：如果该层后面还有兄弟节点，就继续向下画竖线
    for (var level = 1; level < depth; level++) {
      final hasMore = level - 1 < ancestorsHasMore.length && ancestorsHasMore[level - 1];
      if (!hasMore) continue;
      final x = step * level - step / 2;
      canvas.drawLine(Offset(x, 0), Offset(x, size.height), paint);
    }

    // 当前层的连接线：竖线到中点，再横着连到节点
    final x = step * depth - step / 2;
    canvas.drawLine(
      Offset(x, 0),
      Offset(x, isLast ? midY : size.height),
      paint,
    );
    canvas.drawLine(Offset(x, midY), Offset(x + step / 2 - 2, midY), paint);
  }

  @override
  bool shouldRepaint(_TreeLinesPainter old) =>
      old.depth != depth ||
      old.isLast != isLast ||
      old.ancestorsHasMore != ancestorsHasMore ||
      old.color != color;
}
