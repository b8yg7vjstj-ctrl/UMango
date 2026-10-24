// SPDX-License-Identifier: Apache-2.0
//
// 叠加式补间链 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// 原始设计（PCL）：
//   PCL 的补间值是**增量**，而且同一个通道的多个补间是**并行**跑的：
//   `AniRun` 每帧算 `delta = value × ease.GetDelta(现在, 之前)`，
//   然后把 delta **加到目标的当前值上**。
//   所以"MyPageRight"的入场位移是：
//       初值 Y = -16
//       并行段1: AaTranslateY(+5,  250ms, AniEaseOutFluent)
//       并行段2: AaTranslateY(+11, 350ms, AniEaseOutBack)
//   任意时刻的 Y = -16 + 5×f1(t) + 11×f2(t)
//   两段**不是先后**，而是同时推进、各自贡献自己的增量。
//   终态 = -16 + 5 + 11 = 0。
//
//   这与"顺序动画"（第二段等第一段跑完）**完全不同** —— 顺序动画的终态是
//   -16 → -11 → 0（分三段走），而 PCL 是 -16 → 一路回弹到 0（两段叠加）。
//   PCL 的回弹感正来自这个叠加：BackOut 段会先冲过 0 再回来。
//
// UMango 的差异：
//   1. PCL 每帧用 `ease.GetDelta(t1, t0)` 累加（增量积分）；这里直接用
//      `ease(t)` 乘以段增量 —— 二者数学等价（增量的累加 = 值的差分），
//      但后者不需要保存上一帧的进度，纯函数、可单测、可重放。
//   2. PCL 的通道反射（`dynamic`）在 Core 里不存在；这里只负责算出数值，
//      由宿主的 <see cref="IAnimatable"/> 写回。

namespace UMango.Core.Animation;

/// <summary>
/// 一段叠加补间：自身延迟之后，在时长内按缓动把 <see cref="Delta"/> 贡献到通道上。
/// </summary>
/// <param name="Delta">该段的总增量（例如「往下 5 像素」）。</param>
/// <param name="DurationMs">该段的时长（毫秒）。</param>
/// <param name="Easing">该段的缓动（默认线性）。</param>
/// <param name="DelayMs">该段自己的延迟（毫秒）。PCL 里每个 <c>Aa*</c> 都能带自己的 delay。</param>
public sealed record TweenStage(double Delta, int DurationMs, Easing? Easing = null, int DelayMs = 0)
{
    /// <summary>实际时长（不小于 0）。</summary>
    public int EffectiveDurationMs => Math.Max(0, DurationMs);

    /// <summary>该段结束的时刻（毫秒，相对动画起点）。</summary>
    public int EndMs => Math.Max(0, DelayMs) + EffectiveDurationMs;
}

/// <summary>
/// 并行叠加的补间链：多个段**同时**推进，各自贡献自己的增量。
///
/// 对应 PCL 的"同一通道上挂多个 <c>Aa*</c> 补间"（借鉴设计）。
///
/// 用法：
/// <code>
/// var chain = new OffsetChainAnimation(
///     startValue: -16d,
///     stages: [
///         new TweenStage(+5d,  250, new FluentOutEasing(EasePower.Middle)),
///         new TweenStage(+11d, 350, new BackEaseWithPowerEasing(EasePower.Middle)),
///     ],
///     durationFrames: 21, fps: 60);
/// </code>
/// 终值 = -16 + 5 + 11 = 0。
/// </summary>
public sealed class OffsetChainAnimation : AnimationBase
{
    private readonly double _startValue;
    private readonly IReadOnlyList<TweenStage> _stages;
    private readonly int _durationMs;

    /// <param name="startValue">通道的初始值（动画开始前的值）。</param>
    /// <param name="stages">叠加的段。</param>
    /// <param name="durationFrames">总帧数（不小于各段结束时刻所需帧数）。</param>
    /// <param name="minValue">下限（null 表示不夹）。</param>
    /// <param name="maxValue">上限（null 表示不夹）。</param>
    /// <param name="relative">
    /// true = 相对于目标**当前值**叠加（PCL 的语义：把 delta 加到当前值上）；
    /// false = 相对于 <paramref name="startValue"/> 绝对计算。
    /// </param>
    public OffsetChainAnimation(
        double startValue,
        IReadOnlyList<TweenStage> stages,
        int durationFrames,
        double? minValue = null,
        double? maxValue = null,
        bool relative = false)
    {
        _startValue = startValue;
        _stages = stages ?? [];
        _durationMs = _stages.Count == 0 ? 0 : _stages.Max(s => s.EndMs);
        _min = minValue;
        _max = maxValue;
        _relative = relative;
        Duration = Math.Max(0, durationFrames);
    }

    private readonly double? _min;
    private readonly double? _max;
    private readonly bool _relative;

    private int _duration;
    public int Duration
    {
        get => _duration;
        set => _duration = Math.Max(0, value);
    }

    public override int DurationFrames => _duration;

    /// <summary>动画开始前的通道值。</summary>
    public double StartValue => _startValue;

    /// <summary>参与叠加的段。</summary>
    public IReadOnlyList<TweenStage> Stages => _stages;

    /// <summary>链的总时长（毫秒）—— 由各段的最晚结束时刻决定。</summary>
    public int TotalDurationMs => _durationMs;

    /// <summary>最后一帧实际写入的值（诊断与测试用）。</summary>
    public double LastValue { get; private set; }

    /// <summary>
    /// 相对模式下的基准值快照。
    ///
    /// **必须只抓一次**：每帧都读目标的当前值会让增量逐帧累加
    /// （1、2、3…），那不是"叠加"而是螺旋上升。
    /// </summary>
    private double? _relativeStart;

    /// <summary>相对模式下的基准值（诊断与测试用）。</summary>
    public double? RelativeStartValue => _relativeStart;

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        var elapsedMs = _durationMs <= 0 ? 0d : progress * _durationMs;

        var accumulated = 0d;
        foreach (var stage in _stages)
        {
            var stageMs = stage.EffectiveDurationMs;
            if (stageMs <= 0)
            {
                // 零时长段：到达自身延迟后立刻贡献全部增量（PCL 的 time=0 同样是瞬时）。
                if (elapsedMs >= Math.Max(0, stage.DelayMs)) accumulated += stage.Delta;
                continue;
            }

            // 每段有自己的延迟：延迟未到时该段贡献为 0。
            var stageElapsed = elapsedMs - Math.Max(0, stage.DelayMs);
            if (stageElapsed <= 0d) continue;

            var t = Math.Clamp(stageElapsed / stageMs, 0d, 1d);
            var ease = stage.Easing ?? Easing.Of(EasingKind.Linear);
            accumulated += stage.Delta * ease.Ease(t);
        }

        // 相对模式下叠加到目标的**起始值** —— PCL 的语义。
        // 绝对模式下从 startValue 起算（终值 = startValue + 全部增量）。
        if (_relative) _relativeStart ??= ToDouble(target.GetValue());
        var baseValue = _relative ? _relativeStart!.Value : _startValue;
        var value = baseValue + accumulated;

        if (_min is not null) value = Math.Max(_min.Value, value);
        if (_max is not null) value = Math.Min(_max.Value, value);

        LastValue = value;
        target.SetValue(value);
    }

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0d
    };

    public override void Reset()
    {
        base.Reset();
        _relativeStart = null;
    }
}
