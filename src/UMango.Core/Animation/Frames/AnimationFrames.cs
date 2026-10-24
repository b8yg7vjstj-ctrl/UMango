using UMango.Core.Animation.Values;

namespace UMango.Core.Animation.Frames;

/// <summary>
/// 动作帧：把一个回调当作一帧。
///
/// 与 PCL-CE 的 <c>Core/ActionAnimationFrame.cs</c> 对应（Apache-2.0）。
/// PCL 那边是 <c>struct</c>，这里是 <c>readonly record struct</c> —— 语义一样，
/// 但 record 带来值相等与 <c>ToString</c>，调试期更友好。
/// </summary>
public readonly record struct ActionAnimationFrame(Action Action) : IAnimationFrame
{
    public Action GetAction() => Action;
}

/// <summary>
/// 从 A 到 B 的帧：持有目标、增量值与起始值，动作 = 把「起始值 + 增量」写回目标。
///
/// 与 PCL-CE 的 <c>Core/FromToAnimationFrame&lt;T&gt;</c> 对应（Apache-2.0）。
///
/// PCL 的 <c>GetAction()</c> 里直接调 <c>ValueProcessorManager.Add(StartValue, Value)</c> ——
/// 也就是**在动作被取出时**就把绝对值算好，而不是在动作被**执行**时。
/// 这个差别很关键：若在执行时才算，而起始值在那之前被别的动画改过，
/// 写回的值就会漂移。UMango 沿用 PCL 的这个语义（取出即固化），
/// 并在注释里写明原因。
/// </summary>
public readonly record struct FromToAnimationFrame<T>(
    IAnimatable Target,
    T Value,
    T StartValue,
    ValueProcessorRegistry? Registry = null) : IAnimationFrame
{
    /// <summary>本帧的绝对值：起始值 + 增量（用注册表算，找不到处理器时退回相加）。</summary>
    public T GetAbsoluteValue()
    {
        var registry = Registry ?? ValueProcessorRegistry.Default;
        var sum = registry.Add(StartValue, Value);
        return sum is T typed ? typed : Value;
    }

    public Action GetAction()
    {
        var target = Target;
        // 取出动作时就把绝对值算好并固化下来（见类型注释）。
        var absolute = GetAbsoluteValue();
        return () => target.SetValue(absolute);
    }
}
