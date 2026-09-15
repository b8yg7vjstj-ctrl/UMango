/// 依赖关系树可视化（PCL-CE 观感）。
///
/// 需求：
///   · 版本列表依赖关系树可视化，**清晰展示依赖链条**；
///   · 依赖项可单独点击，支持「直接安装依赖」或「跳转至依赖版本详情页」；
///   · 依赖故障要有兜底分支（网络异常、源访问失败时的呈现）。
///
/// 呈现方式：缩进 + 连接线（而不是紧凑的横排图）——
/// 包依赖的深度通常 2~4 层、每层节点数不定，纵向缩进树在桌面宽屏上可读性最好，
/// 也天然适配"列表逐条入场"的动画方向。
///
/// 视觉契约：`docs/DESIGN_PCL_CE.md`。本文件只用 PCL token 与 PCL 组件
/// （[PclColors] / [PclMetrics] / [PclTypography] / PclPanel / PclIcon /
/// PclIconButton / PclTag），不再使用 Material 默认观感（Card / ListTile /
/// 主题色驱动的 Icon 等），以保证与 PCL-CE 复刻的其余页面观感一致。
library;

import 'package:flutter/material.dart';

import '../animation/motion_widgets.dart';
import '../models/umango_models.dart';
import '../theme/pcl_theme.dart';
import 'pcl_widgets.dart';
import 'source_icon.dart';

// ---------------------------------------------------------------------------
// 本文件专用的契约数值（集中放置，方便与设计文档对照）
// ---------------------------------------------------------------------------

/// 每一层缩进的宽度：连接线的阶梯步长（原实现值，语义为「层级可读性」）。
const double _indentStep = 20;

/// 未验证状态的警示色 `#8A5300`。
///
/// 该值不在 `pcl_theme.dart` 的 token 表里，但 `DESIGN_PCL_CE.md` 与本次改造
/// 契约都明确要求「未验证 / 降级」用这个琥珀色 —— 因此按契约硬编码，
/// 不做 token 化改写（token 表由主题文件维护，不在此处扩展）。
const Color _warnAmber = Color(0xFF8A5300);

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
            // 截断提示是"刻意的保护"而非错误，故用最浅蓝底 c8 的 PclPanel，
            // 文字保持 12 号 gray3 的说明层级。
            child: PclPanel(
              radius: PclMetrics.windowRadius,
              color: PclColors.c8,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const PclIcon(name: 'info', size: 14, color: PclColors.gray3),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      widget.tree.truncated
                          ? '依赖树已达到深度或节点上限，更深层未展开（这是刻意的保护，避免依赖爆炸拖垮界面）'
                          : '为保证流畅，仅显示前 ${widget.maxVisibleNodes} 个节点',
                      style: PclTypography.captionStyle,
                    ),
                  ),
                ],
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
///
/// 视觉：PclPanel（圆角 6）+ 12 号 gray3 说明文字；异常项保留各自的语义色
/// （未验证=琥珀、缺失/冲突=error、成环=c2、全部成功=c3），
/// 这样"有没有问题"依然能在一行内被扫出来。
class _TreeSummary extends StatelessWidget {
  const _TreeSummary({required this.tree, required this.nodeCount});

  final DependencyTree tree;
  final int nodeCount;

  @override
  Widget build(BuildContext context) {
    final s = tree.summary;

    return PclPanel(
      radius: PclMetrics.windowRadius,
      color: Colors.white,
      border: Border.all(color: PclColors.gray5),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      child: Wrap(
        spacing: 12,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(
            '共 $nodeCount 个依赖节点',
            style: PclTypography.captionStyle.copyWith(fontWeight: FontWeight.w600),
          ),
          if (s.maxDepth > 0)
            _Stat(
              icon: 'layers',
              text: '最深 ${s.maxDepth} 层',
              tone: PclColors.gray3,
            ),
          if (s.unverified > 0)
            _Stat(
              icon: 'help-circle',
              text: '${s.unverified} 个未验证',
              tone: _warnAmber,
            ),
          if (s.missing > 0)
            _Stat(
              icon: 'x',
              text: '${s.missing} 个找不到匹配版本',
              tone: PclColors.error,
            ),
          if (s.conflict > 0)
            _Stat(
              icon: 'triangle-alert',
              text: '${s.conflict} 个版本冲突',
              tone: PclColors.error,
            ),
          if (s.cyclic > 0)
            _Stat(
              icon: 'refresh-cw',
              text: '${s.cyclic} 个依赖成环（已截断）',
              tone: PclColors.c2,
            ),
          if (!s.hasProblems && s.unverified == 0)
            const _Stat(
              icon: 'circle-check',
              text: '依赖全部解析成功',
              tone: PclColors.c3,
            ),
        ],
      ),
    );
  }
}

/// 统计头部里的一项：lucide 图标（14）+ 12 号文字，颜色按语义给定。
class _Stat extends StatelessWidget {
  const _Stat({required this.icon, required this.text, required this.tone});

  final String icon;
  final String text;
  final Color tone;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        PclIcon(name: icon, size: 14, color: tone),
        const SizedBox(width: 4),
        Text(
          text,
          style: PclTypography.captionStyle.copyWith(color: tone),
        ),
      ],
    );
  }
}

/// 依赖故障的兜底呈现：把降级原因明确列出来，而不是静默显示一棵残缺的树。
///
/// 视觉：PclPanel + lucide `triangle-alert` + [PclColors.redBack] 底 + 12 号文字。
class _WarningsPanel extends StatelessWidget {
  const _WarningsPanel({required this.warnings});

  final List<String> warnings;

  @override
  Widget build(BuildContext context) {
    return ListEntrance(
      index: 0,
      child: PclPanel(
        radius: PclMetrics.windowRadius,
        color: PclColors.redBack,
        border: Border.all(color: PclColors.redLight),
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const PclIcon(
                  name: 'triangle-alert',
                  size: 14,
                  color: PclColors.redDark,
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    '解析过程中有 ${warnings.length} 项降级处理（网络异常或源访问失败的兜底）',
                    style: PclTypography.captionStyle.copyWith(
                      fontWeight: FontWeight.w600,
                      color: PclColors.redDark,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            // 保留 SelectableText：降级原因常常要贴给上游排障，必须可复制。
            ...warnings.take(6).map(
                  (w) => Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: SelectableText(
                      '· $w',
                      style: PclTypography.captionStyle.copyWith(
                        color: PclColors.redDark,
                      ),
                    ),
                  ),
                ),
            if (warnings.length > 6)
              Text(
                '… 其余 ${warnings.length - 6} 项省略',
                style: PclTypography.captionStyle.copyWith(
                  color: PclColors.redDark,
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// 单行依赖节点：缩进 + 连接线 + 状态 + 可点操作。
///
/// 视觉契约：行高 36（与 [PclMetrics.listItemHeight] / `PclListItem` 一致）、
/// 状态图标 16、行文字 13、版本号等宽 12 号 gray3、
/// 操作按钮 [PclIconButton]`(size: 22)`。
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

  static const double _indentWidth = _indentStep;

  /// 行高 36：与 PclListItem 一致，保证依赖树与左栏列表的节奏相同。
  static const double _rowHeight = PclMetrics.listItemHeight;

  @override
  Widget build(BuildContext context) {
    final (Color stateColor, String stateIcon, String stateText) = _stateVisual(node);

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
                    color: PclColors.gray5,
                  ),
                ),
              ),
            ],
            // 折叠箭头：复用 PclIconButton，天然带 hover 底 c6 与 tooltip
            SizedBox(
              width: 20,
              child: hasChildren
                  ? PclIconButton(
                      icon: collapsed ? 'chevron-right' : 'chevron-down',
                      size: 20,
                      tooltip: collapsed ? '展开依赖' : '折叠依赖',
                      onPressed: onToggle,
                    )
                  : const SizedBox.shrink(),
            ),
            const SizedBox(width: 4),
            // 状态图标（16），文字状态放 tooltip 里，避免行内信息过载
            Tooltip(
              message: stateText,
              child: PclIcon(name: stateIcon, size: 16, color: stateColor),
            ),
            const SizedBox(width: 8),
            // 名字（可点击跳转详情页）：hover 时变主强调色，是 PCL 的链接观感，
            // 因此不用 Material 的 InkWell 水波纹。
            Flexible(
              child: _HoverBuilder(
                enabled: onOpen != null,
                builder: (BuildContext context, bool hovered) => GestureDetector(
                  onTap: onOpen,
                  behavior: HitTestBehavior.opaque,
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 4, horizontal: 2),
                    child: Text(
                      node.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: PclTypography.body,
                        fontWeight: FontWeight.w600,
                        fontFamilyFallback: PclTypography.fontFallback,
                        color: (hovered && onOpen != null)
                            ? PclColors.c3
                            : PclColors.c1,
                        decoration: onOpen != null
                            ? TextDecoration.underline
                            : null,
                        decorationColor: PclColors.gray5,
                      ),
                    ),
                  ),
                ),
              ),
            ),
            const SizedBox(width: 8),
            // 解析出的版本 / 声明的范围：等宽 12 号 gray3，压在 gray8 圆角底上
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: PclColors.gray8,
                borderRadius: BorderRadius.circular(PclMetrics.tagRadius),
              ),
              child: Text(
                node.displayVersion,
                style: const TextStyle(
                  fontSize: PclTypography.caption,
                  fontFamily: 'monospace',
                  color: PclColors.gray3,
                ),
              ),
            ),
            const SizedBox(width: 8),
            if (node.kind != DependencyKind.runtime)
              Padding(
                padding: const EdgeInsets.only(right: 6),
                child: Text(
                  node.kind.displayName,
                  style: PclTypography.labelStyle.copyWith(
                    color: PclColors.gray4,
                  ),
                ),
              ),
            const Spacer(),
            // 依赖自身的指标
            DownloadBadge(stats: node.downloads, compact: true),
            const SizedBox(width: 8),
            // 两种操作：直接安装 / 跳转详情
            if (onInstall != null) ...[
              PclIconButton(
                icon: 'download',
                size: 22,
                tooltip: '直接安装该依赖',
                onPressed: onInstall,
              ),
              const SizedBox(width: 4),
            ],
            if (onOpen != null)
              PclIconButton(
                icon: 'external-link',
                size: 22,
                tooltip: '跳转到该依赖的版本详情',
                onPressed: onOpen,
              ),
            const SizedBox(width: 4),
          ],
        ),
      ),
    );
  }

  /// 节点状态 → (颜色, lucide 图标名, 说明文字)。
  ///
  /// 颜色按契约：resolved 用 c3、unverified 用 #8A5300、missing 与 conflict 用
  /// error、cyclic 用 c2；truncated 是"没展开"的中性态，用 gray3。
  (Color, String, String) _stateVisual(DependencyNode node) {
    return switch (node.state) {
      DependencyState.resolved => (
          PclColors.c3,
          'circle-check',
          '版本已解析',
        ),
      DependencyState.unverified => (
          _warnAmber,
          'help-circle',
          '未验证：${node.reason ?? '无法获取该依赖的元数据'}',
        ),
      DependencyState.missing => (
          PclColors.error,
          'x',
          '没有满足 ${node.requirement} 的版本',
        ),
      DependencyState.conflict => (
          PclColors.error,
          'triangle-alert',
          '版本冲突：${node.reason ?? ''}',
        ),
      DependencyState.cyclic => (
          PclColors.c2,
          'refresh-cw',
          '依赖成环，已在此截断：${node.reason ?? ''}',
        ),
      DependencyState.truncated => (
          PclColors.gray3,
          'chevron-right',
          '未展开（达到深度上限）',
        ),
    };
  }
}

/// 画缩进用的竖线与拐角。用线条呈现层级，比纯缩进更能看清"链条"。
///
/// 线条颜色固定 [PclColors.gray5]（PCL 的分隔线色），不再跟随 Material 主题。
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

// ---------------------------------------------------------------------------
// 私有辅助
// ---------------------------------------------------------------------------

/// 悬停侦测（私有）：`pcl_widgets.dart` 的 hover 包装没有导出，
/// 而依赖名需要在 hover 时切换颜色 —— 这里用一个极小的局部实现，
/// 避免为了一个 hover 状态去扩公共 API。
class _HoverBuilder extends StatefulWidget {
  const _HoverBuilder({required this.builder, this.enabled = true});

  /// 依据 hover 状态构建子树。
  final Widget Function(BuildContext context, bool hovered) builder;

  /// 是否响应悬停（禁用时恒为 false，且不把手型光标换成 click）。
  final bool enabled;

  @override
  State<_HoverBuilder> createState() => _HoverBuilderState();
}

class _HoverBuilderState extends State<_HoverBuilder> {
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
