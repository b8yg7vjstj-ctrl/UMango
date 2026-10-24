namespace UMango.Core.Animation.Values;

/// <summary>
/// 值处理器：把 0~1 的进度插值成具体类型的值。
///
/// 与 PCL-CE 的 `UI/Animation/ValueProcessor/`（9 文件 297 行）对应（Apache-2.0）。
/// 分离出来的意义：动画引擎不需要知道"动的是什么类型" ——
/// 位移动画插值 double、颜色动画插值 RGB、可见性动画在阈值处切换，
/// 引擎只按帧把进度交给处理器。
/// </summary>
public interface IValueProcessor
{
    /// <summary>本处理器能处理的类型。</summary>
    Type ValueType { get; }

    /// <summary>
    /// 是否能处理给定的运行时类型。
    ///
    /// 默认按 <see cref="ValueType"/> 精确或可赋值匹配 —— 但这对**值类型不够用**：
    /// `double.IsAssignableFrom(typeof(int))` 是 false，于是 int 值会找不到处理器。
    /// 因此数值型处理器会覆盖本方法做显式判断。
    /// </summary>
    bool CanProcess(Type valueType)
        => ValueType.IsAssignableFrom(valueType);

    /// <summary>按进度插值。progress 已由缓动处理过，这里只做线性插值。</summary>
    object? Process(object? from, object? to, double progress);

    // ---- 算术运算 ----
    //
    // PCL-CE 的 `ValueProcessor/IValueProcessor.cs`（Apache-2.0）把 Filter/Add/
    // Subtract/Scale/DefaultValue/Equal 六个方法都放在接口里，因为它要支持
    // "相对值动画"（Relative）—— 相对模式下动画值是与**目标当前值**相加的，
    // 而不是直接赋值。
    //
    // UMango 之前只有插值（Process），所以做不了相对值动画。
    // 这里把算术运算补上：Filter 已被 Process 取代（进度插值不需要"过滤"），
    // 其余五个照搬语义。

    /// <summary>两值相加。相对值动画靠它把偏移量加到当前值上。</summary>
    object? Add(object? value1, object? value2);

    /// <summary>两值相减。</summary>
    object? Subtract(object? value1, object? value2);

    /// <summary>按因子缩放。</summary>
    object? Scale(object? value, double factor);

    /// <summary>该类型的初始值（相对值动画的基准）。</summary>
    object? DefaultValue();

    /// <summary>两值是否相等（用容差比较，避免浮点误差导致"每帧都算变了"）。</summary>
    bool Equal(object? value1, object? value2);
}

/// <summary>double 插值（位移、透明度、缩放、进度条都用它）。</summary>
public sealed class DoubleValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(double);

    /// <summary>接受所有数值类型（int/long/float/decimal 都能按 double 插值）。</summary>
    public bool CanProcess(Type valueType) => IsNumeric(valueType);

    private static bool IsNumeric(Type t) => Type.GetTypeCode(t) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or
        TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    public object Process(object? from, object? to, double progress)
    {
        var a = ToDouble(from);
        var b = ToDouble(to);
        return a + (b - a) * progress;
    }

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0d
    };

    public object? Add(object? value1, object? value2) => ToDouble(value1) + ToDouble(value2);

    public object? Subtract(object? value1, object? value2) => ToDouble(value1) - ToDouble(value2);

    public object? Scale(object? value, double factor) => ToDouble(value) * factor;

    public object? DefaultValue() => 0d;

    /// <summary>容差 1e-6（与 PCL-CE 的 <c>DoubleValueProcessor.Equal</c> 一致）。</summary>
    public bool Equal(object? value1, object? value2)
        => Math.Abs(ToDouble(value1) - ToDouble(value2)) < 1e-6;
}

/// <summary>
/// 处理器注册表：按目标值类型挑选处理器。
///
/// **找不到处理器时返回 null 而不是抛异常**：动画是表现层的东西，
/// 缺一个处理器不该让整个动画流程崩掉 —— 调用方会跳过该动画（并已经在日志里留痕）。
/// </summary>
public sealed class ValueProcessorRegistry
{
    private readonly List<IValueProcessor> _processors = [];

    public ValueProcessorRegistry(IEnumerable<IValueProcessor>? processors = null)
    {
        _processors.AddRange(processors ?? [new DoubleValueProcessor()]);
    }

    public IReadOnlyList<IValueProcessor> All => _processors;

    /// <summary>
    /// 默认注册表（**只含 double 处理器** —— 这是历史行为，很多调用方直接用它）。
    ///
    /// 帧对象在构造时可能拿不到注册表（例如从别处反序列化出来的帧），
    /// 这时用默认注册表兜底 —— 动画是表现层的东西，缺个处理器不该崩。
    /// 要全套处理器请用 <see cref="CreateFull"/>。
    /// </summary>
    public static ValueProcessorRegistry Default { get; } = new();

    /// <summary>
    /// 创建一个**含全部内置处理器**的注册表。
    ///
    /// 动画要动的不只是数字：颜色（淡入/渐变）、缩放与旋转变换、点、厚度、矩阵
    /// 都得有对应处理器，否则 <see cref="Process"/> 会返回 null，动画静默不生效。
    /// 这些处理器对应 PCL-CE 的 <c>PCL.Core/UI/Animation/ValueProcessor/</c>（Apache-2.0）。
    /// </summary>
    public static ValueProcessorRegistry CreateFull() => new(
    [
        new DoubleValueProcessor(),
        new NColorValueProcessor(),
        new NPointValueProcessor(),
        new NThicknessValueProcessor(),
        new NMatrixValueProcessor(),
        new NScaleTransformValueProcessor(),
        new NRotateTransformValueProcessor(),
    ]);

    public void Register(IValueProcessor processor) => _processors.Add(processor);

    /// <summary>找能处理该运行时类型（或其可赋值类型）的处理器。</summary>
    public IValueProcessor? Find(Type? valueType)
    {
        if (valueType is null) return null;

        // 先精确匹配，再问各处理器自己能不能处理（数值型的判断在处理器里）
        var exact = _processors.FirstOrDefault(p => p.ValueType == valueType);
        if (exact is not null) return exact;

        return _processors.FirstOrDefault(p => p.CanProcess(valueType));
    }

    /// <summary>按进度插值；找不到处理器时返回 null。</summary>
    public object? Process(object? from, object? to, double progress)
    {
        var processor = Find(from?.GetType() ?? to?.GetType());
        return processor?.Process(from, to, progress);
    }

    // ---- 算术运算的注册表入口 ----
    //
    // 找不到处理器时：**加/减返回 null、缩放返回原值、相等按引用比较**。
    // 不抛异常的理由与 Process 相同：动画是表现层的东西，
    // 缺一个处理器不该让整个动画流程崩掉。

    /// <summary>两值相加；找不到处理器时返回 null。</summary>
    public object? Add(object? value1, object? value2)
    {
        var processor = Find(value1?.GetType() ?? value2?.GetType());
        return processor?.Add(value1, value2);
    }

    /// <summary>两值相减；找不到处理器时返回 null。</summary>
    public object? Subtract(object? value1, object? value2)
    {
        var processor = Find(value1?.GetType() ?? value2?.GetType());
        return processor?.Subtract(value1, value2);
    }

    /// <summary>按因子缩放；找不到处理器时原样返回。</summary>
    public object? Scale(object? value, double factor)
    {
        var processor = Find(value?.GetType());
        return processor?.Scale(value, factor) ?? value;
    }

    /// <summary>该类型的初始值；找不到处理器时返回 null。</summary>
    public object? DefaultValue(Type? valueType)
    {
        var processor = Find(valueType);
        return processor?.DefaultValue();
    }

    /// <summary>两值是否相等；找不到处理器时按引用比较。</summary>
    public bool Equal(object? value1, object? value2)
    {
        var processor = Find(value1?.GetType() ?? value2?.GetType());
        return processor is null ? ReferenceEquals(value1, value2) : processor.Equal(value1, value2);
    }
}
