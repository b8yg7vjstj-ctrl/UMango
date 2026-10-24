// SPDX-License-Identifier: Apache-2.0
//
// 动画值处理器 —— 采用自 PCL-CE（PCL-Community/PCL-CE，Apache-2.0）。
// 原始文件：PCL.Core/UI/Animation/ValueProcessor/{IValueProcessor,DoubleValueProcessor,
//   NColorValueProcessor,NRotateTransformValueProcessor,NScaleTransformValueProcessor,
//   PointValueProcessor,ThicknessValueProcessor,MatrixValueProcessor,ValueProcessorManager}.cs
//
// UMango 侧的改动：
//   1. PCL 的 `IValueProcessor<T>` 是**泛型**接口 + 全局静态 `ValueProcessorManager`。
//      UMango 已有非泛型 `IValueProcessor` + 实例化 `ValueProcessorRegistry`
//      （见 `Values/IValueProcessor.cs`）—— 泛型版本无法表达"这个值是 int 但要按 double 插值"
//      这种情况，而 UMango 的 double 处理器正是这么做的。**沿用 UMango 已有抽象**，
//      不引入 PCL 的泛型层。
//   2. PCL 的 Point / Thickness / Matrix 处理器直接用 WPF 的 `System.Windows.Point` 等类型
//      → 无法在 Core（net10.0 支）编译。改为对应的 NPoint / NThickness / NMatrix。
//   3. PCL 的 `ValueProcessorManager.Add(object, object)` 在类型不一致时抛异常；
//      UMango 的注册表在找不到处理器时返回 null（动画是表现层的东西，缺个处理器不该崩）。
//   4. PCL 的处理器**没有插值方法**（插值在各自的 FromToAnimation 类里）。
//      UMango 把插值统一收敛到 `IValueProcessor.Process`，因此这里补上。

using UMango.Core.Animation.Values;

namespace UMango.Core.Animation.Values;

/// <summary>颜色插值处理器。与 PCL 的 <c>NColorValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NColorValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NColor);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NColor a || to is not NColor b) return to;

        var t = (float)Math.Clamp(progress, 0, 1);
        return new NColor(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }

    /// <summary>
    /// 过滤：负分量夹到 0。与 PCL 一致（PCL 只夹下界，不夹上界）。
    /// </summary>
    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NColor a || value2 is not NColor b) return value2;
        return new NColor(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NColor a || value2 is not NColor b) return value2;
        return new NColor(a.R - b.R, a.G - b.G, a.B - b.B, a.A - b.A);
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NColor c) return value;
        var f = (float)factor;
        return new NColor(c.R * f, c.G * f, c.B * f, c.A * f);
    }

    public object? DefaultValue() => new NColor();

    public bool Equal(object? value1, object? value2)
        => value1 is NColor a && value2 is NColor b && a.Equals(b);
}

/// <summary>点插值处理器。与 PCL 的 <c>PointValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NPointValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NPoint);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NPoint a || to is not NPoint b) return to;
        var t = Math.Clamp(progress, 0, 1);
        return new NPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NPoint a || value2 is not NPoint b) return value2;
        return a + b;
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NPoint a || value2 is not NPoint b) return value2;
        return a - b;
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NPoint p) return value;
        return p * factor;
    }

    public object? DefaultValue() => new NPoint();

    public bool Equal(object? value1, object? value2)
        => value1 is NPoint a && value2 is NPoint b && a.Equals(b);
}

/// <summary>厚度插值处理器。与 PCL 的 <c>ThicknessValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NThicknessValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NThickness);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NThickness a || to is not NThickness b) return to;
        var t = Math.Clamp(progress, 0, 1);
        return new NThickness(
            a.Left + (b.Left - a.Left) * t,
            a.Top + (b.Top - a.Top) * t,
            a.Right + (b.Right - a.Right) * t,
            a.Bottom + (b.Bottom - a.Bottom) * t);
    }

    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NThickness a || value2 is not NThickness b) return value2;
        return a + b;
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NThickness a || value2 is not NThickness b) return value2;
        return a - b;
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NThickness t) return value;
        return t * factor;
    }

    public object? DefaultValue() => new NThickness();

    public bool Equal(object? value1, object? value2)
        => value1 is NThickness a && value2 is NThickness b && a.Equals(b);
}

/// <summary>矩阵插值处理器。与 PCL 的 <c>MatrixValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NMatrixValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NMatrix);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NMatrix a || to is not NMatrix b) return to;
        var t = Math.Clamp(progress, 0, 1);
        return new NMatrix(
            a.M11 + (b.M11 - a.M11) * t,
            a.M12 + (b.M12 - a.M12) * t,
            a.M21 + (b.M21 - a.M21) * t,
            a.M22 + (b.M22 - a.M22) * t,
            a.OffsetX + (b.OffsetX - a.OffsetX) * t,
            a.OffsetY + (b.OffsetY - a.OffsetY) * t);
    }

    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NMatrix a || value2 is not NMatrix b) return value2;
        return a + b;
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NMatrix a || value2 is not NMatrix b) return value2;
        return a - b;
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NMatrix m) return value;
        return m * factor;
    }

    public object? DefaultValue() => new NMatrix();

    public bool Equal(object? value1, object? value2)
        => value1 is NMatrix a && value2 is NMatrix b && a.Equals(b);
}

/// <summary>缩放变换插值处理器。与 PCL 的 <c>NScaleTransformValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NScaleTransformValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NScaleTransform);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NScaleTransform a || to is not NScaleTransform b) return to;
        var t = (float)Math.Clamp(progress, 0, 1);
        return new NScaleTransform(
            a.ScaleX + (b.ScaleX - a.ScaleX) * t,
            a.ScaleY + (b.ScaleY - a.ScaleY) * t,
            a.CenterX + (b.CenterX - a.CenterX) * t,
            a.CenterY + (b.CenterY - a.CenterY) * t);
    }

    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NScaleTransform a || value2 is not NScaleTransform b) return value2;
        return a + b;
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NScaleTransform a || value2 is not NScaleTransform b) return value2;
        return a - b;
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NScaleTransform s) return value;
        return s * (float)factor;
    }

    public object? DefaultValue() => new NScaleTransform();

    public bool Equal(object? value1, object? value2)
        => value1 is NScaleTransform a && value2 is NScaleTransform b && a.Equals(b);
}

/// <summary>旋转变换插值处理器。与 PCL 的 <c>NRotateTransformValueProcessor</c> 对应（Apache-2.0）。</summary>
public sealed class NRotateTransformValueProcessor : IValueProcessor
{
    public Type ValueType => typeof(NRotateTransform);

    public object? Process(object? from, object? to, double progress)
    {
        if (from is not NRotateTransform a || to is not NRotateTransform b) return to;
        var t = (float)Math.Clamp(progress, 0, 1);
        return new NRotateTransform(
            a.Angle + (b.Angle - a.Angle) * t,
            a.CenterX + (b.CenterX - a.CenterX) * t,
            a.CenterY + (b.CenterY - a.CenterY) * t);
    }

    public object? Add(object? value1, object? value2)
    {
        if (value1 is not NRotateTransform a || value2 is not NRotateTransform b) return value2;
        return a + b;
    }

    public object? Subtract(object? value1, object? value2)
    {
        if (value1 is not NRotateTransform a || value2 is not NRotateTransform b) return value2;
        return a - b;
    }

    public object? Scale(object? value, double factor)
    {
        if (value is not NRotateTransform r) return value;
        return r * (float)factor;
    }

    public object? DefaultValue() => new NRotateTransform();

    public bool Equal(object? value1, object? value2)
        => value1 is NRotateTransform a && value2 is NRotateTransform b && a.Equals(b);
}
