/// 动画组件：把 [AnimationConfig] 变成实际效果。
///
/// 这里只做两件事，但都是需求里点名的：
///   1. [ListEntrance] —— 列表项按配置的方向与节奏逐条入场；
///   2. [showUmangoDialog] —— 弹窗按配置的方向滑入。
///
/// 所有实现都遵循同一条原则：**配置说不动画就真的不动画**，
/// 不做"反正就一点点"的偷偷动效。系统开了"减少动态效果"时同样直接跳过。
library;

import 'package:flutter/material.dart';

import 'animation_config.dart';

/// 列表入场动画包装器。
///
/// ```dart
/// ListView.builder(
///   itemBuilder: (context, i) => ListEntrance(index: i, child: Card(...)),
/// )
/// ```
class ListEntrance extends StatefulWidget {
  const ListEntrance({
    super.key,
    required this.index,
    required this.child,
    this.axisKey,
  });

  /// 在列表中的序号，决定启动延迟
  final int index;
  final Widget child;

  /// 变化时重新播放动画（例如切换了包源 / 重新搜索后，用新 key 触发入场）
  final Object? axisKey;

  @override
  State<ListEntrance> createState() => _ListEntranceState();
}

class _ListEntranceState extends State<ListEntrance> with SingleTickerProviderStateMixin {
  late final AnimationController _controller;
  AnimationConfig? _config;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(vsync: this, duration: const Duration(milliseconds: 320));
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _startIfNeeded();
  }

  @override
  void didUpdateWidget(ListEntrance oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.axisKey != widget.axisKey || oldWidget.index != widget.index) {
      _startIfNeeded(force: true);
    } else {
      _startIfNeeded();
    }
  }

  void _startIfNeeded({bool force = false}) {
    final config = AnimationScope.of(context);
    final reduced = AnimationScope.prefersReducedMotion(context);
    final shouldAnimate = config.enabled && !reduced;

    // 配置变化时重新评估：比如用户刚在设置里打开了动画
    if (!force && _config == config) return;
    _config = config;

    _controller.duration = config.listDuration;
    if (!shouldAnimate || config.listEntrance == ListEntranceStyle.none) {
      _controller.value = 1;
      return;
    }
    _controller.value = 0;
    final delay = config.delayForIndex(widget.index);
    Future<void>.delayed(delay, () {
      if (mounted) _controller.forward();
    });
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final config = _config ?? const AnimationConfig();
    final reduced = AnimationScope.prefersReducedMotion(context);

    if (!config.enabled ||
        reduced ||
        config.listEntrance == ListEntranceStyle.none) {
      return widget.child;
    }

    final animation = CurvedAnimation(parent: _controller, curve: config.curve);
    final begin = config.beginOffset();

    Widget result = SlideTransition(
      position: Tween<Offset>(begin: begin, end: Offset.zero).animate(animation),
      child: widget.child,
    );

    // 位移之外统一叠一层淡入，避免"凭空滑入"的突兀感
    if (config.animatesOpacity) {
      result = FadeTransition(opacity: animation, child: result);
    }
    return result;
  }
}

/// 按配置弹出对话框（弹窗动画风格的应用点）。
///
/// 用 [showGeneralDialog] 而不是 [showDialog]：后者固定使用系统缩放动画，
/// 无法改向量；前者允许完全自定义 transitionBuilder。
Future<T?> showUmangoDialog<T>({
  required BuildContext context,
  required WidgetBuilder builder,
  bool barrierDismissible = true,
  String barrierLabel = '对话框',
}) {
  final config = AnimationScope.of(context);
  final reduced = AnimationScope.prefersReducedMotion(context);

  if (!config.enabled ||
      reduced ||
      config.dialogMotion == DialogMotionStyle.none) {
    // 不动画时也要能弹出，只是不动
    return showGeneralDialog<T>(
      context: context,
      barrierDismissible: barrierDismissible,
      barrierLabel: barrierLabel,
      barrierColor: Colors.black.withValues(alpha: 0.32),
      transitionDuration: Duration.zero,
      pageBuilder: (context, _, __) => builder(context),
    );
  }

  final begin = config.dialogBeginOffset();
  final isScale = config.dialogMotion == DialogMotionStyle.scaleUp;

  return showGeneralDialog<T>(
    context: context,
    barrierDismissible: barrierDismissible,
    barrierLabel: barrierLabel,
    barrierColor: Colors.black.withValues(alpha: 0.32),
    transitionDuration: config.dialogDuration,
    pageBuilder: (context, _, __) => builder(context),
    transitionBuilder: (context, animation, _, child) {
      final curved = CurvedAnimation(parent: animation, curve: config.curve);
      if (isScale) {
        return FadeTransition(
          opacity: curved,
          child: ScaleTransition(
            scale: Tween<double>(begin: 0.88, end: 1.0).animate(curved),
            child: child,
          ),
        );
      }
      return FadeTransition(
        opacity: curved,
        child: SlideTransition(
          position: Tween<Offset>(begin: begin, end: Offset.zero).animate(curved),
          child: child,
        ),
      );
    },
  );
}

/// 页面切换动画。侧边栏切页时使用，方向固定为轻微的横向淡入，
/// 不参与"列表/弹窗"的风格配置（它属于导航层级，不在这两个需求项内）。
class PageFadeThrough extends StatelessWidget {
  const PageFadeThrough({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    final config = AnimationScope.of(context);
    final reduced = AnimationScope.prefersReducedMotion(context);
    if (!config.enabled || reduced) return child;

    return TweenAnimationBuilder<double>(
      key: ValueKey(child.runtimeType),
      tween: Tween<double>(begin: 0, end: 1),
      duration: const Duration(milliseconds: 220),
      curve: Curves.easeOut,
      builder: (context, t, inner) => Opacity(
        opacity: t,
        child: Transform.translate(offset: Offset(0, (1 - t) * 8), child: inner),
      ),
      child: child,
    );
  }
}

/// 状态提示条（加载中 / 出错 / 空结果）统一入口，带轻微入场。
class StatusBanner extends StatelessWidget {
  const StatusBanner({
    super.key,
    required this.icon,
    required this.title,
    this.message,
    this.action,
    this.tone = StatusTone.neutral,
  });

  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;
  final StatusTone tone;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final (bg, fg) = switch (tone) {
      StatusTone.error => (theme.colorScheme.errorContainer, theme.colorScheme.onErrorContainer),
      StatusTone.warning => (
          const Color(0xFFFFF3E0),
          const Color(0xFF8A5300),
        ),
      StatusTone.success => (
          const Color(0xFFE6F4EA),
          const Color(0xFF1E6B33),
        ),
      StatusTone.neutral => (theme.colorScheme.surfaceContainerHighest, theme.colorScheme.onSurfaceVariant),
    };

    return ListEntrance(
      index: 0,
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(18),
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(12)),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, color: fg, size: 22),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(title,
                      style: theme.textTheme.titleSmall
                          ?.copyWith(color: fg, fontWeight: FontWeight.w600)),
                  if (message != null) ...[
                    const SizedBox(height: 6),
                    SelectableText(message!,
                        style: theme.textTheme.bodySmall?.copyWith(color: fg)),
                  ],
                ],
              ),
            ),
            if (action != null) ...[const SizedBox(width: 12), action!],
          ],
        ),
      ),
    );
  }
}

enum StatusTone { neutral, error, warning, success }
