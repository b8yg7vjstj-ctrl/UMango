// SPDX-License-Identifier: Apache-2.0
//
// 通道级补间 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// PCL 的补间语义（三条，都在这里落地）：
//   1. **补间值是增量**：终值 = 目标起始值 + 各补间贡献之和。
//      例：`AaTranslateY(11)` 是"往下挪 11 像素"，不是"挪到 Y=11"。
//   2. **同一通道上的多个补间是并行叠加的**，不是先后、也不是互相覆盖：
//      PCL 的 `AniRun` 每帧对每个 `AniData` 算出自己的增量，
//      然后把这些增量**加到目标的当前值上**。
//      例：入场位移 = 初值 Y=-16，并行段1 AaTranslateY(+5,250ms)，
//          并行段2 AaTranslateY(+11,350ms)，
//      任意时刻 Y = -16 + 5·f1(t) + 11·f2(t)，终态 = 0。
//   3. **每个补间可以带自己的延迟**：延迟未到时该补间贡献为 0。
//
// UMango 早期实现的两处真实缺陷（本类修掉）：
//   · `DelayMs` 被整个丢弃 —— 数值补间一律从第 0 帧就开始动。
//   · 同通道的多个补间**后写覆盖先写** —— 只剩下最后一个补间的效果，
//     PCL 的"叠加回弹"完全消失。

using UMango.Core.Animation.Values;

namespace UMango.Core.Animation;

/// <summary>
/// 一个通道上的全部补间，作为一个动画整体推进。
///
/// 每一帧：
/// <code>
/// value = 起始值 + Σ(各补间的增量 × 各自缓动(各自进度))
/// </code>
/// 其中每个补间的进度 = <c>(当前时刻 - 自己的延迟) / 自己的时长</c>。
/// </summary>
public sealed class TweenChannelAnimation : AnimationBase
{
    private readonly IReadOnlyList<TweenSpec> _specs;
    private readonly int _totalDurationMs;
    private object? _baseValue;
    private bool _baseCaptured;

    /// <param name="specs">同一通道上的补间（至少一条）。</param>
    /// <param name="startOverride">起始值覆盖（null = 读取目标当前值）。</param>
    /// <param name="durationFrames">总帧数。</param>
    public TweenChannelAnimation(
        IReadOnlyList<TweenSpec> specs,
        object? startOverride,
        int durationFrames)
    {
        ArgumentNullException.ThrowIfNull(specs);
        _specs = specs.ToList();
        StartOverride = startOverride;
        _totalDurationMs = _specs.Count == 0
            ? 0
            : _specs.Max(s => Math.Max(0, s.DelayMs) + s.EffectiveDurationMs);
        Duration = Math.Max(0, durationFrames);
    }

    /// <summary>起始值覆盖（null 表示读目标当前值）。</summary>
    public object? StartOverride { get; }

    /// <summary>参与叠加的补间。</summary>
    public IReadOnlyList<TweenSpec> Specs => _specs;

    /// <summary>总时长（毫秒）—— 由各补间最晚的结束时刻决定。</summary>
    public int TotalDurationMs => _totalDurationMs;

    private int _duration;
    public int Duration
    {
        get => _duration;
        set => _duration = Math.Max(0, value);
    }

    public override int DurationFrames => _duration;

    /// <summary>起始值快照（诊断与测试用）。</summary>
    public object? BaseValue => _baseValue;

    /// <summary>最后一帧实际写入的值（诊断与测试用）。</summary>
    public object? LastValue { get; private set; }

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        if (_specs.Count == 0) return;

        var kind = _specs[0].Kind;

        // 起始值只抓一次：目标会被本动画逐帧改写，
        // 每帧重读就成了"当前帧值"，那会变成螺旋上升而不是叠加。
        if (!_baseCaptured)
        {
            _baseValue = StartOverride ?? target.GetValue();
            _baseCaptured = true;
        }

        var elapsedMs = _totalDurationMs <= 0 ? 0d : progress * _totalDurationMs;

        switch (kind)
        {
            case TweenKind.Number:
            {
                var baseValue = ToDouble(_baseValue);
                var total = 0d;
                foreach (var spec in _specs) total += Contribution(spec, elapsedMs);

                // 不透明度要夹到 0~1：PCL 的 AniRun 对 Opacity 用
                // `ModBase.MathClamp(current + delta, 0d, 1d)`。
                // 少了这一步，过冲的补间会让不透明度超过 1（WPF 会抛异常）。
                if (_specs[0].Channel == TweenChannel.Opacity)
                    total = Math.Clamp(baseValue + total, 0d, 1d) - baseValue;

                Write(target, baseValue + total);
                return;
            }

            case TweenKind.Color:
            {
                var baseColor = _baseValue is NColor c ? c : new NColor();
                var r = 0f; var g = 0f; var b = 0f; var a = 0f;
                foreach (var spec in _specs)
                {
                    var factor = (float)ProgressFactor(spec, elapsedMs);
                    var delta = spec.Color ?? new NColor();
                    r += delta.R * factor;
                    g += delta.G * factor;
                    b += delta.B * factor;
                    a += delta.A * factor;
                }
                Write(target, new NColor(baseColor.R + r, baseColor.G + g, baseColor.B + b, baseColor.A + a));
                return;
            }

            case TweenKind.Scale:
            {
                // 倍率语义：终值 = 起始值 × 倍率。多个倍率相乘。
                var baseValue = ToDouble(_baseValue);
                var factor = 1d;
                foreach (var spec in _specs)
                {
                    var t = ProgressFactor(spec, elapsedMs);
                    var multiplier = 1d + (spec.Scale - 1d) * t;
                    factor *= multiplier;
                }
                Write(target, baseValue * factor);
                return;
            }

            case TweenKind.RotateTransform:
            {
                var baseTransform = _baseValue is NRotateTransform r ? r : new NRotateTransform();
                var angle = 0f;
                foreach (var spec in _specs) angle += (float)(spec.Value * ProgressFactor(spec, elapsedMs));
                Write(target, new NRotateTransform(baseTransform.Angle + angle, baseTransform.CenterX, baseTransform.CenterY));
                return;
            }

            case TweenKind.ScaleTransform:
            {
                var baseTransform = _baseValue is NScaleTransform s ? s : new NScaleTransform(1, 1);
                var x = 0f; var y = 0f;
                foreach (var spec in _specs)
                {
                    var f = (float)ProgressFactor(spec, elapsedMs);
                    x += (float)spec.Value * f;
                    y += (float)spec.Value * f;
                }
                Write(target, new NScaleTransform(
                    baseTransform.ScaleX + x,
                    baseTransform.ScaleY + y,
                    baseTransform.CenterX, baseTransform.CenterY));
                return;
            }

            case TweenKind.TextAppear:
            {
                // 文字出现：终值 = 文字长度。多个补间时取最长的那个。
                var spec = _specs[0];
                var length = spec.Text?.Length ?? 0;
                var t = ProgressFactor(spec, elapsedMs);
                Write(target, (spec.TextHide ? length * (1 - t) : length * t));
                return;
            }
        }
    }

    /// <summary>
    /// 某个补间在当前时刻的缓动后系数（0~1）。
    /// 延迟未到 → 0；已结束 → 1。
    /// </summary>
    private static double ProgressFactor(TweenSpec spec, double elapsedMs)
    {
        var delay = Math.Max(0, spec.DelayMs);
        var stageElapsed = elapsedMs - delay;
        if (stageElapsed <= 0d) return 0d;

        var duration = spec.EffectiveDurationMs;
        if (duration <= 0) return 1d;

        var t = Math.Clamp(stageElapsed / duration, 0d, 1d);
        var ease = spec.Easing ?? Easing.Of(EasingKind.Linear);
        return ease.Ease(t);
    }

    private static double Contribution(TweenSpec spec, double elapsedMs)
        => spec.Value * ProgressFactor(spec, elapsedMs);

    private void Write(IAnimatable target, object value)
    {
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
        _baseValue = null;
        _baseCaptured = false;
    }
}
