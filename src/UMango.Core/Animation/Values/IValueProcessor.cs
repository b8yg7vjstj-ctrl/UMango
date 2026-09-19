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
}
