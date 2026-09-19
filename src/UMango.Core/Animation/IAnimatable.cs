namespace UMango.Core.Animation;

/// <summary>
/// 可被动画驱动的目标。
///
/// 与 PCL-CE 的 `PCL.Core/UI/Animation/Animatable/IAnimatable.cs` 对应（Apache-2.0）：
/// 那边是 <c>GetValue()</c> / <c>SetValue(object)</c> 这组最小接口 —— 动画只关心
/// "读一个值、写一个值"，不关心背后是 WPF 依赖属性、Avalonia 属性还是普通对象。
/// 这正是该引擎能跨框架的原因，因此**原样保留这个抽象**。
/// </summary>
public interface IAnimatable
{
    object? GetValue();
    void SetValue(object? value);
}

/// <summary>把一个取值/写值委托对包装成 <see cref="IAnimatable"/>（便利实现，也用于测试）。</summary>
public sealed class DelegateAnimatable(Func<object?> getter, Action<object?> setter) : IAnimatable
{
    public object? GetValue() => getter();
    public void SetValue(object? value) => setter(value);
}

/// <summary>持有单个值的可动画对象（不依赖任何界面框架，测试与简单场景用）。</summary>
public sealed class ValueAnimatable(object? initial = null) : IAnimatable
{
    private object? _value = initial;

    public object? Value
    {
        get => _value;
        set => _value = value;
    }

    public object? GetValue() => _value;
    public void SetValue(object? value) => _value = value;
}
