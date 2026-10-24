// SPDX-License-Identifier: Apache-2.0
//
// 子项级联（stagger）动画编排 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Controls/MyPageLeft.cs 与 MyPageRight.cs
// （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
// 不复制其代码；本文件为独立重写。
//
// 原始设计（PCL）：
//   页面切换时，不是整页一起动，而是**逐个子项**依次入场：
//     · MyPageRight（右侧内容区）：每个子项
//         初始 opacity = 0、TranslateY = -16
//         AaOpacity(+1,  100ms, delay, AniEaseOutFluent(Weak))
//         AaTranslateY(+5,  250ms, delay, AniEaseOutFluent())
//         AaTranslateY(+11, 350ms, delay, AniEaseOutBack())
//       每放一个子项，delay += 25ms。
//     · MyPageLeft（左侧菜单区）：每个子项
//         初始 opacity = 0、TranslateX = -25
//         AaOpacity(+1,  100ms, delay, AniEaseOutFluent(Weak))
//         AaTranslateX(+5,  200ms, delay, AniEaseOutFluent())
//         AaTranslateX(+20, 300ms, delay, AniEaseOutBack(Weak))
//       delay 增量 = max(15 - 序号, 7) × 2 —— 越靠后的项越密（前松后紧）。
//     · 退场（MyPageLeft）：每个子项
//         AaOpacity(-当前不透明度, 50ms, delay)
//         AaTranslateX(-6, 50ms, delay)
//       delay = round(70 / 总数 × 序号) —— 整体 70ms 内均分。
//
// UMango 的差异：
//   1. PCL 直接遍历可视树、在运行期反射改 WPF 属性；这里把"每个子项该动什么、
//      延迟多少"抽成**纯数据**（<see cref="StaggerItemPlan"/>），
//      宿主（App 层）只负责把数据落到具体控件上。
//   2. PCL 的两套级联（左栏 X 方向、右栏 Y 方向）在这里统一为一个
//      <see cref="StaggerAxis"/> 参数，数值仍按 PCL 原版分档。
//   3. PCL 的"文本块淡入到 0.6 而不是 1.0"这种**按类型分叉**的行为，
//      这里降级为调用方可选的 <see cref="StaggerItemPlan.OpacityTarget"/>。

namespace UMango.Core.Animation;

/// <summary>级联动画的方向。</summary>
public enum StaggerAxis
{
    /// <summary>纵向（PCL 的 <c>MyPageRight</c>：Y 位移）。</summary>
    Vertical,

    /// <summary>横向（PCL 的 <c>MyPageLeft</c>：X 位移）。</summary>
    Horizontal,
}

/// <summary>级联动画的阶段。</summary>
public enum StaggerPhase
{
    /// <summary>入场（PCL 的 <c>TriggerShowAnimation</c>）。</summary>
    Enter,

    /// <summary>退场（PCL 的 <c>TriggerHideAnimation</c>）。</summary>
    Exit,
}

/// <summary>
/// 单个子项的级联动画计划（纯数据）。
///
/// 描述"这个子项要动哪些通道、动多少、什么时候动"。
/// </summary>
public sealed record StaggerItemPlan
{
    /// <summary>子项序号（0 起）。</summary>
    public int Index { get; init; }

    /// <summary>该子项的动画延迟（毫秒）。</summary>
    public int DelayMs { get; init; }

    /// <summary>入场时的初始位移（负值 = 从外侧滑入）。</summary>
    public double FromOffset { get; init; }

    /// <summary>入场时的不透明度初值。</summary>
    public double FromOpacity { get; init; }

    /// <summary>不透明度目标（PCL 右栏为 1.0；PCL 左栏的 TextBlock 为 0.6）。</summary>
    public double OpacityTarget { get; init; } = 1d;

    /// <summary>该子项要执行的补间。</summary>
    public IReadOnlyList<TweenSpec> Specs { get; init; } = [];

    /// <summary>是否为可见子项（不可见的项不参与动画）。</summary>
    public bool Visible { get; init; } = true;
}

/// <summary>
/// 级联动画的档位参数。
///
/// 数值全部取自 PCL-CE 原版（<c>MyPageLeft.cs</c> / <c>MyPageRight.cs</c>），
/// 逐条对应，改动前请先对照原版。
/// </summary>
public sealed record StaggerProfile
{
    // ---------- 入场（MyPageRight，Y 方向） ----------

    /// <summary>入场初始 Y 偏移（PCL 实测 -16）。</summary>
    public double EnterFromY { get; init; } = -16d;

    /// <summary>入场中段 Y 增量（PCL 实测 +5）。</summary>
    public double EnterMidOffset { get; init; } = 5d;

    /// <summary>入场末段 Y 增量（PCL 实测 +11）。</summary>
    public double EnterToOffset { get; init; } = 11d;

    /// <summary>淡入时长（PCL 实测 100ms）。</summary>
    public int FadeMs { get; init; } = 100;

    /// <summary>中段位移时长（PCL 实测 250ms）。</summary>
    public int MidMs { get; init; } = 250;

    /// <summary>末段位移时长（PCL 实测 350ms）。</summary>
    public int ToMs { get; init; } = 350;

    /// <summary>每个子项的延迟增量（PCL 右栏实测 25ms）。</summary>
    public int EnterDelayStepMs { get; init; } = 25;

    // ---------- 入场（MyPageLeft，X 方向） ----------

    /// <summary>入场初始 X 偏移（PCL 实测 -25）。</summary>
    public double EnterFromX { get; init; } = -25d;

    /// <summary>入场中段 X 增量（PCL 左栏实测 +5）。</summary>
    public double EnterMidOffsetX { get; init; } = 5d;

    /// <summary>入场末段 X 增量（PCL 左栏实测 +20）。</summary>
    public double EnterToOffsetX { get; init; } = 20d;

    /// <summary>中段位移时长（PCL 左栏实测 200ms）。</summary>
    public int MidMsX { get; init; } = 200;

    /// <summary>末段位移时长（PCL 左栏实测 300ms）。</summary>
    public int ToMsX { get; init; } = 300;

    /// <summary>
    /// 入场延迟增量的"递减"参数（PCL 左栏实测 <c>max(15 - 序号, 7) × 2</c>）。
    /// 设为 0 表示改成固定步长（即右栏的 25ms 恒定增量）。
    /// </summary>
    public int EnterDelayBaseMs { get; init; } = 15;

    /// <summary>入场延迟增量的"下限"（PCL 左栏实测 7）。</summary>
    public int EnterDelayFloorMs { get; init; } = 7;

    /// <summary>入场延迟增量的倍数（PCL 左栏实测 2）。</summary>
    public int EnterDelayMultiplier { get; init; } = 2;

    // ---------- 退场 ----------

    /// <summary>退场位移量（PCL 左栏实测 -6）。</summary>
    public double ExitOffset { get; init; } = -6d;

    /// <summary>退场时长（PCL 左栏实测 50ms）。</summary>
    public int ExitMs { get; init; } = 50;

    /// <summary>退场整体时长（PCL 左栏实测 70ms，延迟在 0~70 之间均分）。</summary>
    public int ExitTotalMs { get; init; } = 70;

    /// <summary>
    /// 退场的**固定步长**（毫秒）。
    ///
    /// PCL 右栏（<c>MyPageRight.TriggerExitAnimation</c>）用的是**恒定 15ms 步进**：
    /// 每个子项的延迟 = 序号 × 15，与子项总数无关。
    /// PCL 左栏（<c>MyPageLeft.TriggerHideAnimation</c>）用的是**按比例均分**：
    /// 延迟 = round(70 / 总数 × 序号) —— 总数越多，每个子项的间隔越密。
    ///
    /// 这两套算式**不同**，别混：右栏是"按序号"，左栏是"按比例"。
    /// </summary>
    public int ExitDelayStepMs { get; init; }

    /// <summary>
    /// 退场是否用**固定步长**（true）而不是按比例均分（false）。
    /// 见 <see cref="ExitDelayStepMs"/>。
    /// </summary>
    public bool ExitUseFixedStep { get; init; }

    // ---------- 缓动 ----------

    /// <summary>淡入缓动（PCL 用 <c>AniEaseOutFluent(Weak)</c> = 1-(1-t)^2）。</summary>
    public Easing FadeEase { get; init; } = new FluentOutEasing(EasePower.Weak);

    /// <summary>中段位移缓动（PCL 用 <c>AniEaseOutFluent()</c> = 1-(1-t)^3）。</summary>
    public Easing MidEase { get; init; } = new FluentOutEasing(EasePower.Middle);

    /// <summary>
    /// 末段位移缓动。
    /// PCL 右栏用 <c>AniEaseOutBack()</c>（默认 Middle 档，指数 1.5）；
    /// PCL 左栏用 <c>AniEaseOutBack(Weak)</c>（指数 2）—— 两者力度不同。
    /// </summary>
    public Easing ToEase { get; init; } = new BackEaseWithPowerEasing(EasePower.Middle);

    /// <summary>
    /// PCL 原版数值（右栏：Y 方向，固定 25ms 步进）。
    ///
    /// 与 <see cref="PageMotionProfile.Pcl"/> 的关系：那是**整页**的入场，
    /// 这里是**子项**的级联 —— PCL 两处都用同一批数值，但 PCL 的 MyPageRight
    /// 同时驱动了整页与子项，UMango 把两者分开。
    /// </summary>
    public static StaggerProfile PclRight { get; } = new()
    {
        EnterFromY = -16d,
        EnterMidOffset = 5d,
        EnterToOffset = 11d,
        FadeMs = 100,
        MidMs = 250,
        ToMs = 350,
        EnterDelayStepMs = 25,
        // 右栏是恒定 25ms 步进 → 关闭递减逻辑
        EnterDelayBaseMs = 0,
        EnterDelayFloorMs = 0,
        EnterDelayMultiplier = 1,
        ToEase = new BackEaseWithPowerEasing(EasePower.Middle),
        // 退场：PCL 右栏用恒定 15ms 步进（MyPageRight.cs:648）+ 70ms 时长
        ExitDelayStepMs = 15,
        ExitUseFixedStep = true,
        ExitMs = 70,
    };

    /// <summary>PCL 原版数值（左栏：X 方向，延迟逐项递减）。</summary>
    public static StaggerProfile PclLeft { get; } = new()
    {
        EnterFromX = -25d,
        EnterMidOffsetX = 5d,
        EnterToOffsetX = 20d,
        FadeMs = 100,
        MidMsX = 200,
        ToMsX = 300,
        EnterDelayStepMs = 0,
        EnterDelayBaseMs = 15,
        EnterDelayFloorMs = 7,
        EnterDelayMultiplier = 2,
        ToEase = new BackEaseWithPowerEasing(EasePower.Weak),
        // 退场：PCL 左栏用按比例均分（MyPageLeft.cs:120）+ 50ms 时长
        ExitMs = 50,
        ExitTotalMs = 70,
        ExitUseFixedStep = false,
    };
}

/// <summary>
/// 把 PCL 的级联入场/退场规则编译成一组 <see cref="StaggerItemPlan"/>。
///
/// 对应 PCL 的 <c>MyPageLeft.TriggerShowAnimation</c> / <c>TriggerHideAnimation</c>
/// 与 <c>MyPageRight.TriggerShowAnimation</c>（借鉴设计）。
/// </summary>
public static class StaggerPlanner
{
    /// <summary>
    /// 编排一组子项的级联动画。
    /// </summary>
    /// <param name="itemCount">子项总数。</param>
    /// <param name="axis">方向。</param>
    /// <param name="phase">阶段。</param>
    /// <param name="profile">档位参数（默认 PCL 原版）。</param>
    /// <param name="visibleCount">可见子项数（不可见的项不参与动画，PCL 会跳过它们）。</param>
    /// <param name="opacityTarget">不透明度目标（默认 1.0）。</param>
    public static IReadOnlyList<StaggerItemPlan> Build(
        int itemCount,
        StaggerAxis axis,
        StaggerPhase phase,
        StaggerProfile? profile = null,
        int? visibleCount = null,
        double opacityTarget = 1d)
        => Build(itemCount, axis, phase, profile, null, visibleCount, opacityTarget);

    /// <summary>
    /// 编排一组子项的级联动画（可按索引逐项指定可见性）。
    /// </summary>
    /// <param name="itemCount">子项总数。</param>
    /// <param name="axis">方向。</param>
    /// <param name="phase">阶段。</param>
    /// <param name="profile">档位参数（默认 PCL 原版）。</param>
    /// <param name="isVisible">
    /// 逐项可见性判定（按索引）。为 null 时用 <paramref name="visibleCount"/> 的
    /// 「前 N 项可见」语义。
    ///
    /// PCL 的原版是**任意**跳过不可见项（折叠的子项夹在中间也照样跳过），
    /// 所以需要精确表达时用这个重载。
    /// </param>
    /// <param name="visibleCount">可见子项数（仅在 <paramref name="isVisible"/> 为 null 时生效）。</param>
    /// <param name="opacityTarget">不透明度目标（默认 1.0）。</param>
    public static IReadOnlyList<StaggerItemPlan> Build(
        int itemCount,
        StaggerAxis axis,
        StaggerPhase phase,
        StaggerProfile? profile,
        Func<int, bool>? isVisible,
        int? visibleCount,
        double opacityTarget = 1d)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);

        profile ??= axis == StaggerAxis.Vertical ? StaggerProfile.PclRight : StaggerProfile.PclLeft;

        var visible = visibleCount ?? itemCount;
        if (visible < 0) visible = 0;

        var plans = new List<StaggerItemPlan>(itemCount);
        var delay = 0;
        var visibleIndex = 0;

        // 可见项总数 —— 退场的按比例延迟要用它做分母。
        var totalVisible = isVisible is not null
            ? Enumerable.Range(0, itemCount).Count(isVisible)
            : Math.Min(visible, itemCount);

        for (var index = 0; index < itemCount; index++)
        {
            // 不可见的子项不参与动画 —— 但**不占用**序号，
            // 否则可见项之间的延迟间隔会被拉开（PCL 只对可见项调 delay）。
            var itemVisible = isVisible is not null ? isVisible(index) : visibleIndex < visible;
            if (!itemVisible)
            {
                plans.Add(new StaggerItemPlan { Index = index, Visible = false, DelayMs = delay });
                continue;
            }

            var plan = phase == StaggerPhase.Enter
                ? BuildEnter(index, visibleIndex, axis, profile, delay, opacityTarget)
                : BuildExit(index, visibleIndex, totalVisible, axis, profile, delay, opacityTarget);

            plans.Add(plan);
            delay += NextDelayStep(profile, visibleIndex);
            visibleIndex++;
        }

        return plans;
    }

    /// <summary>
    /// 计算第 <paramref name="visibleIndex"/> 个可见子项之后，延迟应该增加多少。
    ///
    /// PCL 左栏：<c>max(15 - 序号, 7) × 2</c>（前松后紧）；
    /// PCL 右栏：恒定 25ms。
    /// </summary>
    private static int NextDelayStep(StaggerProfile profile, int visibleIndex)
    {
        if (profile.EnterDelayBaseMs <= 0) return profile.EnterDelayStepMs;
        var step = Math.Max(profile.EnterDelayBaseMs - visibleIndex, profile.EnterDelayFloorMs);
        return step * profile.EnterDelayMultiplier;
    }

    private static StaggerItemPlan BuildEnter(
        int index, int visibleIndex, StaggerAxis axis, StaggerProfile profile, int delay, double opacityTarget)
    {
        var isVertical = axis == StaggerAxis.Vertical;

        var fromOffset = isVertical ? profile.EnterFromY : profile.EnterFromX;
        var midOffset = isVertical ? profile.EnterMidOffset : profile.EnterMidOffsetX;
        var toOffset = isVertical ? profile.EnterToOffset : profile.EnterToOffsetX;
        var midMs = isVertical ? profile.MidMs : profile.MidMsX;
        var toMs = isVertical ? profile.ToMs : profile.ToMsX;

        var channel = isVertical ? TweenChannel.TranslateY : TweenChannel.TranslateX;

        var specs = new List<TweenSpec>
        {
            TweenFactory.AaOpacity(opacityTarget, profile.FadeMs, delay, profile.FadeEase),
            TweenFactory.AaTranslate(channel, midOffset, midMs, delay, profile.MidEase),
            TweenFactory.AaTranslate(channel, toOffset, toMs, delay, profile.ToEase),
        };

        return new StaggerItemPlan
        {
            Index = index,
            DelayMs = delay,
            FromOffset = fromOffset,
            FromOpacity = 0d,
            OpacityTarget = opacityTarget,
            Specs = specs,
        };
    }

    private static StaggerItemPlan BuildExit(
        int index, int visibleIndex, int totalVisible, StaggerAxis axis, StaggerProfile profile, int delay, double opacityTarget)
    {
        var isVertical = axis == StaggerAxis.Vertical;
        var channel = isVertical ? TweenChannel.TranslateY : TweenChannel.TranslateX;

        // PCL 的两处退场延迟算式**不同**，这里按 profile 分流：
        //   · 右栏（MyPageRight.cs:648）：恒定 15ms 步进 —— 延迟 = 序号 × 15，
        //     与子项总数无关（10 个子项的最后一个延迟 135ms）。
        //   · 左栏（MyPageLeft.cs:120）：按比例均分 —— 延迟 = round(70 / 总数 × 序号)，
        //     整个退场在 70ms 内结束（子项越多，每个的间隔越密）。
        var exitDelay = profile.ExitUseFixedStep
            ? visibleIndex * profile.ExitDelayStepMs
            : (int)Math.Round((double)profile.ExitTotalMs / Math.Max(1, totalVisible) * visibleIndex);

        var specs = new List<TweenSpec>
        {
            // PCL 用 -Opacity（减到 0）；这里用 -opacityTarget 表达同一意图。
            TweenFactory.AaOpacity(-opacityTarget, profile.ExitMs, exitDelay, profile.MidEase),
            TweenFactory.AaTranslate(channel, profile.ExitOffset, profile.ExitMs, exitDelay, profile.MidEase),
        };

        return new StaggerItemPlan
        {
            Index = index,
            DelayMs = exitDelay,
            FromOffset = 0d,
            FromOpacity = opacityTarget,
            OpacityTarget = 0d,
            Specs = specs,
        };
    }
}
