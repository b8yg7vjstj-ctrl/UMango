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

    /// <summary>
    /// 为一次**页面切换**创建 PCL 式关键帧绑定。
    ///
    /// PCL 的切页入场不是单段插值，而是三段并行/串联的组合（MyPageRight.cs 实测）：
    ///   · 淡入：0 → 1，100ms，幂缓动 Out
    ///   · 位移：Y 两段 —— 先落到中段（250ms，QuadOut），再落定（350ms，BackOut 回弹）
    ///   · 终态 Y 归 0（PCL 存档值 11 是它"先推开再落回"的实现细节，我们直接落回）
    ///
    /// 位移总量会按 settings.EffectiveSpeedFactor 缩放，时长同样除以倍率，
    /// 与单段 <see cref="CreateEntrance"/> 的语义一致。
    /// </summary>
    public static AnimationBinding CreatePageEnter(
        PageMotionProfile profile,
        IAnimatable offsetX,
        IAnimatable offsetY,
        IAnimatable opacity,
        AnimationSettings settings,
        int fps,
        ValueProcessorRegistry? registry = null)
    {
        var speed = settings.EffectiveSpeedFactor;
        var processors = registry ?? new ValueProcessorRegistry();

        int Frames(int ms) => Math.Max(1, (int)Math.Round(ms / speed / 1000.0 * Math.Clamp(fps, 1, 1000)));

        var fade = new FromToAnimation(0.0, 1.0, Frames(profile.FadeInMs), Easing.Of(profile.FadeInEase), processors);

        // 两段位移只在"第二段真的会移动"时才成立：
        // 若中段与终态相同（无过冲的预设，例如 Fluent），第二段是 0 位移的空动作，
        // 却仍占满 EnterToMs —— 表现就是"位移早就结束、动画组还在空转"的拖沓感。
        // 这种预设应当合并为一段直达，时长取两段之和。
        var secondSegmentMoves = Math.Abs(profile.EnterToY - profile.EnterMidY) > 0.01;
        var ySeq = new SequentialAnimationGroup { Name = "PageEnter:Y" };
        if (secondSegmentMoves)
        {
            ySeq.Add(new FromToAnimation(profile.EnterFromY, profile.EnterMidY,
                Frames(profile.EnterMidMs), Easing.Of(profile.EnterMidEase), processors), offsetY);
            ySeq.Add(new FromToAnimation(profile.EnterMidY, 0.0,
                Frames(profile.EnterToMs), Easing.Of(profile.EnterToEase), processors), offsetY);
        }
        else
        {
            // 单段直达（缓动取中段的，保持"先快后慢"的落定感）
            ySeq.Add(new FromToAnimation(profile.EnterFromY, 0.0,
                Frames(profile.EnterMidMs + profile.EnterToMs), Easing.Of(profile.EnterMidEase), processors), offsetY);
        }

        var group = new ParallelAnimationGroup { Name = "PageEnter" };
        group.Add(fade, opacity);
        // 嵌套组：组的 target 参数被忽略（子动画各带 target），这里传 offsetY 仅作占位
        group.Add(new AnimationBinding(ySeq, offsetY));

        // X 不动：PCL 的切页只有纵向落体，没有横向位移（与"对话框左右滑"不同）
        // X 保持不动，但时长必须等于位移序列的实际时长 ——
        // 否则并行组的结束时间由最长的那条决定，又会引入空转。
        var yTotalMs = profile.EnterMidMs + (secondSegmentMoves ? profile.EnterToMs : 0);
        var xHold = new FromToAnimation(0.0, 0.0, Frames(yTotalMs), Easing.Of(EasingKind.Linear), processors);
        group.Add(xHold, offsetX);

        return new AnimationBinding(group, opacity);
    }
}
