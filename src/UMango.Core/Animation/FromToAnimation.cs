using UMango.Core.Animation.Values;

namespace UMango.Core.Animation;

/// <summary>
/// 从 A 到 B 的插值动画 —— 引擎的主力。
///
/// 与 PCL-CE 的 `Core/FromToAnimationBase.cs` + 根目录的
/// `DoubleFromToAnimation` / `MatrixFromToAnimation` 对应（Apache-2.0）。
/// PCL 为每种值类型各写了一个类；UMango 改为**一个类 + 值处理器**，
/// 因为那些类的差异只在"怎么插值"这一点上，而那正是 <see cref="IValueProcessor"/> 的职责。
/// （PCL 的 `ValueProcessor/` 目录本身就已经把这层抽象出来了，只是没在动画类上贯彻。）
///
/// 支持关键帧：<see cref="Keyframes"/> 为空时是简单的两点插值；
/// 有值时按帧号在关键帧之间插值 —— 这是"补全动画系统"里原先缺的能力。
/// </summary>
public sealed class FromToAnimation : AnimationBase
{
    private readonly ValueProcessorRegistry _registry;

    public FromToAnimation(
        object? from,
        object? to,
        int durationFrames,
        Easing? easing = null,
        ValueProcessorRegistry? registry = null,
        IReadOnlyList<AnimationKeyframe>? keyframes = null,
        AnimationValueType valueType = AnimationValueType.Absolute)
    {
        From = from;
        To = to;
        Duration = Math.Max(0, durationFrames);
        Easing = easing ?? Easing.Of(EasingKind.Linear);
        _registry = registry ?? ValueProcessorRegistry.CreateFull();
        Keyframes = keyframes ?? [];
        ValueType = valueType;
    }

    public object? From { get; set; }
    public object? To { get; set; }

    /// <summary>
    /// 值的解释方式：绝对值还是相对偏移。默认绝对值。
    /// </summary>
    public AnimationValueType ValueType { get; set; }

    private int _duration;
    public int Duration
    {
        get => _duration;
        set => _duration = Math.Max(0, value);
    }

    public override int DurationFrames => _duration;

    public Easing Easing { get; set; }

    /// <summary>关键帧（按进度升序）。空表示只有起止两点。</summary>
    public IReadOnlyList<AnimationKeyframe> Keyframes { get; set; }

    /// <summary>最后一帧实际写入的值（诊断与测试用）。</summary>
    public object? LastValue { get; private set; }

    /// <summary>
    /// 相对值模式下记录的"动画开始时目标的当前值"。
    /// 相对值动画算的是**偏移量**，最后写回「起始值 + 偏移量」——
    /// 若每帧都拿目标的当前值去加，就会逐帧累加（1、2、3…），那不是相对值而是螺旋。
    /// </summary>
    private object? _relativeStart;

    /// <summary>相对值模式下的起始值（诊断与测试用）。</summary>
    public object? RelativeStartValue => _relativeStart;

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        // 先做缓动，再按关键帧分段插值
        var eased = Easing.Ease(progress);

        object? value;
        if (Keyframes.Count == 0)
        {
            value = _registry.Process(From, To, eased) ?? To;
        }
        else
        {
            var (a, b, localProgress) = ResolveKeyframeSegment(eased);
            value = _registry.Process(a, b, localProgress) ?? b;
        }

        if (ValueType == AnimationValueType.Relative)
        {
            // 起始值只在第一帧抓一次：之后目标已被动画改写，
            // 再去读就成了"当前帧的值"，会逐帧累加。
            _relativeStart ??= target.GetValue();

            // 偏移量：从 0 插到 To。From 若非零，说明调用方给了起点偏移。
            var offset = _registry.Subtract(value, From) ?? value;
            var absolute = _registry.Add(_relativeStart, offset) ?? _relativeStart;

            LastValue = absolute;
            target.SetValue(absolute);
            return;
        }

        LastValue = value;
        target.SetValue(value);
    }

    /// <summary>
    /// 复位：相对值模式下必须清掉"起始值"快照，
    /// 否则同一个动画对象第二次播放时，偏移量会加到**上一次**的起始值上。
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        _relativeStart = null;
    }

    /// <summary>
    /// 找到 eased 落在哪一段关键帧里，返回该段起止值与段内进度。
    /// 段内进度必须**再归一化到 0~1** —— 直接用全局进度会让每段都只走一部分。
    /// </summary>
    private (object? From, object? To, double Progress) ResolveKeyframeSegment(double eased)
    {
        // 完整的分段是：[From@0] → 关键帧… → [To@1]。
        //
        // **这里踩过一个真实的坑**：最初只遍历"关键帧之间的区间"，
        // 于是只有一个关键帧时，过了它之后就直接跳到 To（0.7 的进度直接得到终值），
        // 中间那一段完全没有插值 —— 表现是动画"跳"而不是"走"。
        // 修法是显式把 To 当作进度 1.0 处的锚点，于是最后一段始终存在。
        var lastProgress = Keyframes.Count > 0 ? Keyframes[^1].Progress : 0;

        for (var i = -1; i < Keyframes.Count; i++)
        {
            var fromValue = i < 0 ? From : Keyframes[i].Value;
            var fromProgress = i < 0 ? 0 : Keyframes[i].Progress;

            var toValue = i + 1 < Keyframes.Count ? Keyframes[i + 1].Value : To;
            var toProgress = i + 1 < Keyframes.Count ? Keyframes[i + 1].Progress : 1.0;

            if (eased <= toProgress || i == Keyframes.Count - 1)
            {
                var span = toProgress - fromProgress;
                var local = span <= 0 ? 1 : (eased - fromProgress) / span;
                return (fromValue, toValue, Math.Clamp(local, 0, 1));
            }
        }

        // 理论到不了这里（上面的循环总会命中最后一段）；保底返回终值
        _ = lastProgress;
        return (Keyframes.Count > 0 ? Keyframes[^1].Value : From, To, 1);
    }
}

/// <summary>
/// 一个关键帧：在某个进度（0~1）处应为某个值。
///
/// 这是"补全动画系统"里原先缺的能力 —— 之前只有起止两点的插值，
/// 做不了"先弹过头再回位""中段停顿"这类效果。
/// </summary>
public sealed record AnimationKeyframe(double Progress, object? Value);
