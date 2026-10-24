namespace UMango.Core.Animation;

/// <summary>
/// 缓动强度档位。
///
/// 与 PCL-CE 的 `Easings/EasePower.cs` 对应（Apache-2.0）：Weak=2 / Middle=3 / Strong=4 / ExtraStrong=5。
/// </summary>
public enum EasePower
{
    Weak = 2,
    Middle = 3,
    Strong = 4,
    ExtraStrong = 5
}

/// <summary>
/// 把两个缓动接成一段：前 <c>split</c> 走第一个，后半走第二个。
///
/// 与 PCL-CE 的 `Easings/CombinedEasing.cs` 对应（Apache-2.0）。
/// PCL 的实现用「各自缓动到 1，再按 split 加权」的方式拼接：
/// 第一段贡献 <c>split * ease1(t/split)</c>，第二段贡献
/// <c>(1-split) * ease2((t-split)/(1-split)) + split</c>。
/// </summary>
public sealed class CombinedEasing : Easing
{
    private readonly Easing _ease1;
    private readonly Easing _ease2;
    private readonly double _split;

    public CombinedEasing(Easing ease1, Easing ease2, double split = 0.5)
    {
        ArgumentNullException.ThrowIfNull(ease1);
        ArgumentNullException.ThrowIfNull(ease2);

        _ease1 = ease1;
        _ease2 = ease2;

        // PCL 把 split 夹在 (0,1) 开区间 —— 0 或 1 会让某一段的长度为 0 而除零。
        _split = Math.Clamp(split, 0.00001, 0.99999);
    }

    /// <summary>两段的分界点（0~1）。</summary>
    public double Split => _split;

    protected override double EaseCore(double t)
    {
        if (t < _split) return _split * _ease1.Ease(t / _split);

        return (1.0 - _split) * _ease2.Ease((t - _split) / (1.0 - _split)) + _split;
    }
}

/// <summary>
/// 多个缓动按各自时长/延迟/权重混合。
///
/// 与 PCL-CE 的 `Easings/CompositeEasing.cs` 对应（Apache-2.0）。
/// 每个缓动有自己的时长与延迟，权重决定它在结果里占多少；
/// 结果是所有缓动输出的加权和。
/// </summary>
public sealed class CompositeEasing : Easing
{
    private readonly List<CompositePart> _parts;
    private readonly TimeSpan _totalDuration;

    /// <summary>复合缓动的一段。</summary>
    public readonly record struct CompositePart(Easing Easing, TimeSpan Duration, TimeSpan Delay, double Weight);

    public CompositeEasing(params (Easing easing, TimeSpan duration)[] easings)
        : this(easings.Select(e => (e.easing, e.duration, TimeSpan.Zero, EqualWeight(easings.Length))).ToArray())
    {
    }

    public CompositeEasing(params (Easing easing, TimeSpan duration, TimeSpan delay)[] easings)
        : this(easings.Select(e => (e.easing, e.duration, e.delay, EqualWeight(easings.Length))).ToArray())
    {
    }

    public CompositeEasing(params (Easing easing, TimeSpan duration, double weight)[] easings)
        : this(easings.Select(e => (e.easing, e.duration, TimeSpan.Zero, e.weight)).ToArray())
    {
    }

    public CompositeEasing(params (Easing easing, TimeSpan duration, TimeSpan delay, double weight)[] easings)
    {
        if (easings is null || easings.Length == 0)
            throw new ArgumentException("至少需要一个缓动", nameof(easings));

        _parts = new List<CompositePart>(easings.Length);

        long maxTicks = 0;
        foreach (var (easing, duration, delay, weight) in easings)
        {
            ArgumentNullException.ThrowIfNull(easing);
            if (duration <= TimeSpan.Zero) throw new ArgumentException("duration 必须大于零", nameof(easings));

            _parts.Add(new CompositePart(easing, duration, delay, weight));

            var endTicks = (delay + duration).Ticks;
            if (endTicks > maxTicks) maxTicks = endTicks;
        }

        _totalDuration = TimeSpan.FromTicks(maxTicks);
    }

    private static double EqualWeight(int count) => 1.0 / (count > 0 ? count : 1);

    /// <summary>总时长（各段「延迟 + 时长」的最大值）。</summary>
    public TimeSpan TotalDuration => _totalDuration;

    /// <summary>各段明细（诊断用）。</summary>
    public IReadOnlyList<CompositePart> Parts => _parts;

    protected override double EaseCore(double progress)
    {
        // 当前绝对时间
        var elapsed = _totalDuration * progress;
        var value = 0.0;

        foreach (var (easing, duration, delay, weight) in _parts)
        {
            if (weight == 0) continue;

            var localElapsed = elapsed - delay;

            double easingValue;
            if (localElapsed <= TimeSpan.Zero)
            {
                // 还没开始
                easingValue = 0.0;
            }
            else if (localElapsed >= duration)
            {
                // 已经结束：保持最终状态
                easingValue = easing.Ease(1.0);
            }
            else
            {
                var localProgress = localElapsed.TotalSeconds / duration.TotalSeconds;
                easingValue = easing.Ease(localProgress);
            }

            value += easingValue * weight;
        }

        return value;
    }
}

/// <summary>
/// 带"回弹强度"的 Back 缓动。
///
/// 与 PCL-CE 的 `Easings/BackEaseWithPower{In,Out,InOut}.cs` 对应（Apache-2.0）。
/// 与普通 Back 缓动的区别：回弹的超调量由 <see cref="EasePower"/> 决定，
/// 而不是固定的 1.70158。
/// </summary>
public sealed class BackEaseWithPowerEasing : Easing
{
    private enum Direction { In, Out, InOut }

    private readonly Direction _direction;
    private readonly double _power;

    public BackEaseWithPowerEasing(EasePower power = EasePower.Middle, bool isIn = false, bool isInOut = false)
    {
        _direction = isInOut ? Direction.InOut : isIn ? Direction.In : Direction.Out;

        // PCL: _p = 3.0 - power * 0.5
        _power = 3.0 - (double)power * 0.5;
    }

    /// <summary>Back 缓动的指数（PCL 公式 3.0 - power*0.5）。</summary>
    public double Power => _power;

    protected override double EaseCore(double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);

        switch (_direction)
        {
            case Direction.In:
                return Math.Pow(t, _power) * Math.Cos(1.5 * Math.PI * (1.0 - t));
            case Direction.Out:
            {
                var inv = 1.0 - t;
                return 1.0 - Math.Pow(inv, _power) * Math.Cos(1.5 * Math.PI * t);
            }
            default:
            {
                if (t < 0.5)
                {
                    var f = 2.0 * t;
                    return 0.5 * (Math.Pow(f, _power) * Math.Cos(1.5 * Math.PI * (1.0 - f)));
                }
                else
                {
                    var f = 2.0 * (t - 0.5);
                    var inv = 1.0 - f;
                    return 0.5 * (1.0 - Math.Pow(inv, _power) * Math.Cos(1.5 * Math.PI * f)) + 0.5;
                }
            }
        }
    }
}
