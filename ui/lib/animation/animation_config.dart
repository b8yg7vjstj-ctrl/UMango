/// UMango 全局动画配置系统。
///
/// # 设计目标（来自项目需求）
///
/// PCL-CE 原版的动画向量要**做镜像级改造**，并且作为**全局独立配置项**，
/// 用户可在设置里切换风格：
///
/// | 项目 | PCL-CE 原版 | UMango 默认 |
/// |------|-------------|-------------|
/// | 列表入场 | 横向进入 | **纵向进入** |
/// | 弹窗弹出 | 向左滑出 | **向右滑出** |
///
/// 保留 PCL-CE 的页面结构与交互逻辑，只改动画向量 —— 这既能降低视觉层面的
/// 代码相似度，也是真实可用的偏好项（原版向量作为 `classic` 预设一并保留，
/// 用户想换回去随时可以）。
///
/// # 关于"向右滑出"的向量定义（需要显式说明的一点）
///
/// 镜像一个位移向量时有两种可能理解：
///   · A「从左侧滑入、向右移动」：begin = Offset(-1, 0) → Offset.zero
///   · B「从右侧滑出、向左移动」：与"向左"同向，不属于镜像
/// 需求写的是"向右滑出"，取**理解 A**（真正与 PCL-CE 的"向左"互为镜像）。
/// 如果你期望的是"从右边缘弹出"，把设置里的风格切到 `从右侧滑入` 即可 ——
/// 这也是把这套向量做成可配置项的价值之一。
library;

import 'package:flutter/material.dart';

/// 列表入场方向。
enum ListEntranceStyle {
  /// UMango 默认：纵向逐条进入（自下而上）
  verticalStagger('纵向逐条进入', '列表项自下而上依次进入（UMango 默认）'),

  /// PCL-CE 原版向量：横向进入
  horizontalStagger('横向逐条进入', '列表项自左向右依次进入（PCL-CE 原版向量）'),

  /// 从右侧横向进入
  horizontalFromRight('横向从右进入', '列表项自右向左依次进入'),

  /// 只做淡入，不做位移
  fadeOnly('仅淡入', '只改变透明度，不产生位移（低干扰）'),

  /// 关闭
  none('关闭', '列表项直接出现，无动画');

  const ListEntranceStyle(this.displayName, this.description);
  final String displayName;
  final String description;

  bool get isVertical =>
      this == ListEntranceStyle.verticalStagger;
}

/// 弹窗弹出方向。
enum DialogMotionStyle {
  /// UMango 默认：向右滑出（自左向右滑入），与 PCL-CE 的"向左"互为镜像
  slideRightward('向右滑出', '弹窗自左向右滑入（UMango 默认，PCL-CE 向量镜像）'),

  /// PCL-CE 原版向量
  slideLeftward('向左滑出', '弹窗自右向左滑入（PCL-CE 原版向量）'),

  /// 自右侧滑入
  slideFromRight('从右侧滑入', '弹窗自右边缘滑入'),

  /// 缩放弹出
  scaleUp('缩放弹出', '弹窗从小到大缩放出现'),

  /// 关闭
  none('关闭', '弹窗直接出现，无动画');

  const DialogMotionStyle(this.displayName, this.description);
  final String displayName;
  final String description;
}

/// 整套动画风格预设。
enum AnimationPreset {
  umango('UMango 默认', '纵向入场 + 向右滑出'),
  classic('PCL-CE 原版向量', '横向入场 + 向左滑出'),
  gentle('轻柔', '仅淡入 + 缩放弹出'),
  off('关闭动画', '全部禁用（等同系统"减少动态效果"）');

  const AnimationPreset(this.displayName, this.description);
  final String displayName;
  final String description;

  AnimationConfig get config => switch (this) {
        AnimationPreset.umango => const AnimationConfig(
            enabled: true,
            listEntrance: ListEntranceStyle.verticalStagger,
            dialogMotion: DialogMotionStyle.slideRightward,
          ),
        AnimationPreset.classic => const AnimationConfig(
            enabled: true,
            listEntrance: ListEntranceStyle.horizontalStagger,
            dialogMotion: DialogMotionStyle.slideLeftward,
          ),
        AnimationPreset.gentle => const AnimationConfig(
            enabled: true,
            listEntrance: ListEntranceStyle.fadeOnly,
            dialogMotion: DialogMotionStyle.scaleUp,
            listDuration: Duration(milliseconds: 260),
            staggerStep: Duration(milliseconds: 22),
          ),
        AnimationPreset.off => const AnimationConfig(
            enabled: false,
            listEntrance: ListEntranceStyle.none,
            dialogMotion: DialogMotionStyle.none,
          ),
      };
}

/// 全局动画配置。不可变对象，修改走 [copyWith]。
@immutable
class AnimationConfig {
  const AnimationConfig({
    this.enabled = true,
    this.listEntrance = ListEntranceStyle.verticalStagger,
    this.dialogMotion = DialogMotionStyle.slideRightward,
    this.listDuration = const Duration(milliseconds: 320),
    this.dialogDuration = const Duration(milliseconds: 260),
    this.staggerStep = const Duration(milliseconds: 38),
    this.curve = Curves.easeOutCubic,
    this.offsetExtent = 0.22,
  });

  /// 总开关。关闭后所有自定义动画直接跳过（保留系统级交互反馈）。
  final bool enabled;

  final ListEntranceStyle listEntrance;
  final DialogMotionStyle dialogMotion;

  /// 单个列表项入场时长
  final Duration listDuration;

  /// 弹窗动画时长
  final Duration dialogDuration;

  /// 相邻列表项之间的启动延迟（形成"逐条进入"的错落感）
  final Duration staggerStep;

  final Curve curve;

  /// 位移距离，相对组件自身尺寸的比例。0.22 表示移动自身高度的 22%。
  final double offsetExtent;

  /// 某个序号的列表项实际等待多久才开始入场。
  ///
  /// 对延迟做封顶：长列表（比如 200 个依赖节点）如果按序号线性累加，
  /// 最后一项要等好几秒 —— 那样不叫动画，叫卡顿。
  Duration delayForIndex(int index) {
    final clamped = index.clamp(0, 14);
    return Duration(microseconds: staggerStep.inMicroseconds * clamped);
  }

  /// 根据风格计算列表项的起始位移。
  Offset beginOffset() => switch (listEntrance) {
        ListEntranceStyle.verticalStagger => Offset(0, offsetExtent),
        ListEntranceStyle.horizontalStagger => Offset(-offsetExtent, 0),
        ListEntranceStyle.horizontalFromRight => Offset(offsetExtent, 0),
        ListEntranceStyle.fadeOnly => Offset.zero,
        ListEntranceStyle.none => Offset.zero,
      };

  /// 根据风格计算弹窗的起始位移。
  Offset dialogBeginOffset() => switch (dialogMotion) {
        // 自左向右滑入：起点在左侧
        DialogMotionStyle.slideRightward => const Offset(-1, 0),
        // PCL-CE 原版：自右向左滑入，起点在右侧
        DialogMotionStyle.slideLeftward => const Offset(1, 0),
        DialogMotionStyle.slideFromRight => const Offset(1, 0),
        DialogMotionStyle.scaleUp => Offset.zero,
        DialogMotionStyle.none => Offset.zero,
      };

  /// 该风格是否包含透明度变化
  bool get animatesOpacity => enabled && listEntrance != ListEntranceStyle.none;

  AnimationConfig copyWith({
    bool? enabled,
    ListEntranceStyle? listEntrance,
    DialogMotionStyle? dialogMotion,
    Duration? listDuration,
    Duration? dialogDuration,
    Duration? staggerStep,
    Curve? curve,
    double? offsetExtent,
  }) =>
      AnimationConfig(
        enabled: enabled ?? this.enabled,
        listEntrance: listEntrance ?? this.listEntrance,
        dialogMotion: dialogMotion ?? this.dialogMotion,
        listDuration: listDuration ?? this.listDuration,
        dialogDuration: dialogDuration ?? this.dialogDuration,
        staggerStep: staggerStep ?? this.staggerStep,
        curve: curve ?? this.curve,
        offsetExtent: offsetExtent ?? this.offsetExtent,
      );

  /// 当前配置最接近哪个预设（用于设置页高亮预设按钮）
  AnimationPreset? get matchingPreset {
    for (final preset in AnimationPreset.values) {
      final c = preset.config;
      if (c.enabled == enabled &&
          c.listEntrance == listEntrance &&
          c.dialogMotion == dialogMotion) {
        return preset;
      }
    }
    return null;
  }

  @override
  bool operator ==(Object other) =>
      other is AnimationConfig &&
      other.enabled == enabled &&
      other.listEntrance == listEntrance &&
      other.dialogMotion == dialogMotion &&
      other.listDuration == listDuration &&
      other.dialogDuration == dialogDuration &&
      other.staggerStep == staggerStep &&
      other.curve == curve &&
      other.offsetExtent == offsetExtent;

  @override
  int get hashCode => Object.hash(enabled, listEntrance, dialogMotion, listDuration,
      dialogDuration, staggerStep, curve, offsetExtent);
}

/// 把动画配置注入子树。
///
/// 用法：`AnimationScope.of(context)` 取配置；
/// `AnimationScope.update(context, config)` 在设置页修改。
class AnimationScope extends InheritedWidget {
  const AnimationScope({
    super.key,
    required this.config,
    required this.onChanged,
    required super.child,
  });

  final AnimationConfig config;
  final ValueChanged<AnimationConfig> onChanged;

  /// 取当前配置。没有祖先 [AnimationScope] 时返回默认值，
  /// 这样单独预览某个组件时不必套壳。
  static AnimationConfig of(BuildContext context) {
    final scope = context.dependOnInheritedWidgetOfExactType<AnimationScope>();
    return scope?.config ?? const AnimationConfig();
  }

  /// 修改配置；没有祖先时静默忽略（避免在测试环境抛异常）。
  static void update(BuildContext context, AnimationConfig config) {
    final scope = context.getInheritedWidgetOfExactType<AnimationScope>();
    scope?.onChanged(config);
  }

  /// 当前环境是否要求"减少动态效果"（系统无障碍设置）。
  ///
  /// 这是必须尊重的系统级偏好：用户开了"减少动态效果"通常是有生理原因的，
  /// 界面不应该强行播放自定义动画。
  static bool prefersReducedMotion(BuildContext context) =>
      MediaQuery.maybeOf(context)?.disableAnimations ?? false;

  @override
  bool updateShouldNotify(AnimationScope oldWidget) => oldWidget.config != config;
}
