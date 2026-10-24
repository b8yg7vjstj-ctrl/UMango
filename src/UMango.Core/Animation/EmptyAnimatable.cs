namespace UMango.Core.Animation;

/// <summary>
/// 什么都不做的可动画对象。
///
/// 与 PCL-CE 的 `Animatable/EmptyAnimatable.cs` 对应（Apache-2.0）。
/// 用途：某些动画只需要"触发一个效果"，并不需要真的改一个属性 ——
/// 给它挂一个空目标即可，不必为每个动画都造一个真实属性。
/// </summary>
public sealed class EmptyAnimatable : IAnimatable
{
    public static EmptyAnimatable Instance { get; } = new();

    private EmptyAnimatable()
    {
    }

    public object? GetValue() => null;

    public void SetValue(object? value)
    {
        // 空
    }
}

/// <summary>
/// 把「所有者 + 取值/写值委托」包装成 <see cref="IAnimatable"/>。
///
/// 与 PCL-CE 的 `Animatable/ClrAnimatable.cs` 对应（Apache-2.0）。
/// 与 UMango 已有的 <see cref="DelegateAnimatable"/> 的区别：
/// 这个版本**带类型**（<c>ClrAnimatable&lt;TOwner, T&gt;</c>），
/// 写值时能自动做类型转换，调用方不必自己写强制转换。
/// </summary>
public sealed class ClrAnimatable<TOwner, T> : IAnimatable
{
    private readonly TOwner _owner;
    private readonly Func<TOwner, T> _getter;
    private readonly Action<TOwner, T> _setter;

    public ClrAnimatable(
        TOwner owner,
        Func<TOwner, T> getter,
        Action<TOwner, T> setter)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);

        _owner = owner;
        _getter = getter;
        _setter = setter;
    }

    public T GetValue() => _getter(_owner);

    public void SetValue(T value) => _setter(_owner, value);

    object? IAnimatable.GetValue() => GetValue();

    void IAnimatable.SetValue(object? value) => SetValue((T)value!);
}
