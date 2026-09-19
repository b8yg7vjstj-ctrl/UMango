using UMango.Core.Animation.Values;

namespace UMango.Core.Animation;

/// <summary>
/// 把"方向策略"（<see cref="AnimationPlanner"/>）与"帧引擎"（<see cref="AnimationService"/>）接起来。
///
/// 分成两半是有意的：
///   · <see cref="AnimationPlanner"/> 只算**数值**（位移方向、距离、时长、缓动）——
///     需求要求的"动画矢量镜像 PCL-CE 且可全局切换"就落在那里；
///   · 帧引擎只管**怎么播**。
/// 于是"方向政策"可以独立测试（已有 20 余项用例），不必启动任何时钟。
/// </summary>
public static class AnimationPlannerBinding
{
    /// <summary>把帧数从时长换算出来（按帧率）。</summary>
    public static int FramesFor(AnimationSpec spec, int fps)
    {
        if (spec.IsInstant) return 0;
        var safeFps = Math.Clamp(fps, 1, 1000);
        return Math.Max(1, (int)Math.Round(spec.DurationMs / 1000.0 * safeFps));
    }

    /// <summary>
    /// 为一次入场动画创建绑定：水平位移、垂直位移、透明度三者并行。
    ///
    /// 传入的三个 <see cref="IAnimatable"/> 由调用方提供（WPF 侧包 TranslateTransform.X / .Y
    /// 与元素的 Opacity），因此 Core 不需要知道 WPF 的任何东西。
    /// </summary>
    public static AnimationBinding CreateEntrance(
        AnimationSpec spec,
        IAnimatable offsetX,
        IAnimatable offsetY,
        IAnimatable opacity,
        int fps,
        ValueProcessorRegistry? registry = null)
    {
        var frames = FramesFor(spec, fps);
        var easing = Easing.Of(spec.Easing);
        var processors = registry ?? new ValueProcessorRegistry();

        var group = new ParallelAnimationGroup { Name = $"Entrance:{spec.Motion}" };

        // 三个属性各自一条插值动画。位移量为 0 的那条也保留 ——
        // 它的时长仍然决定"何时算演完"，去掉会让并行组的总时长变短。
        group.Add(new FromToAnimation(spec.FromOffsetX, spec.ToOffsetX, frames, easing, processors), offsetX);
        group.Add(new FromToAnimation(spec.FromOffsetY, spec.ToOffsetY, frames, easing, processors), offsetY);
        group.Add(new FromToAnimation(spec.FromOpacity, spec.ToOpacity, frames, easing, processors), opacity);

        return new AnimationBinding(group, opacity);
    }
}
