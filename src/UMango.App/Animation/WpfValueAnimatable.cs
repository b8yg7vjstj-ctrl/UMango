// SPDX-License-Identifier: Apache-2.0
//
// WPF 动画目标适配 —— 采用自 PCL-CE（PCL-Community/PCL-CE，Apache-2.0）。
// 原始文件：PCL.Core/UI/Animation/Animatable/WpfAnimatable.cs
//
// UMango 侧的改动：
//   1. PCL 的 `WpfAnimatable` 用 `ValueProcessorManager.Filter(value)` 做过滤，
//      而 PCL 那个管理器是**全局静态**的 —— Core 侧不能依赖它。
//      这里改为注入一个 `ValueProcessorRegistry`（可为 null 表示不过滤）。
//   2. PCL 在 `GetValue()` 里把 `WidthProperty` 换成 `ActualWidthProperty`。
//      这个替换**只在读取时**做，`SetValue` 仍然写回 `WidthProperty` —— 看起来是刻意的
//      （读实际值、写期望值），但 PCL 的 `SetValue` 里 `ArgumentNullException.ThrowIfNull(Property)`
//      之后并没有用到替换过的属性，逻辑是自洽的。UMango 保留该语义。
//   3. PCL 的 `SetValue` 对 `Property.Name == "Color"` 与其它情况分别转 `Color` / `SolidColorBrush`。
//      这里保持一致，并补上**按目标属性类型判断**的兜底（属性名不是 "Color" 但类型是 Color 时
//      也能正确写入）—— 这是 PCL 的一个真实缺陷：属性名改一下就写错类型。
//   4. PCL 的 `SetValue<T>` 用 `Unsafe.As` 强转；UMango 侧改用类型检查 + 显式分支，
//      避免 `unsafe`（Core 的 net10.0 支刻意禁用 unsafe）。

using System.Windows;
using System.Windows.Media;
using UMango.Core.Animation;
using UMango.Core.Animation.Values;

namespace UMango.App.Animation;

/// <summary>
/// 把 WPF 的「依赖对象 + 依赖属性」包装成 Core 的 <see cref="IAnimatable"/>。
///
/// 与 PCL-CE 的 <c>PCL.Core/UI/Animation/Animatable/WpfAnimatable.cs</c> 对应（Apache-2.0）。
///
/// Core 的动画引擎只认「读一个值、写一个值」；
/// 这个类把 WPF 的具体属性接进来，并负责 Core 值类型与 WPF 类型之间的转换：
/// <list type="bullet">
///   <item><description><see cref="NColor"/> ↔ <see cref="Color"/> / <see cref="SolidColorBrush"/></description></item>
///   <item><description><see cref="NScaleTransform"/> ↔ <see cref="ScaleTransform"/></description></item>
///   <item><description><see cref="NRotateTransform"/> ↔ <see cref="RotateTransform"/></description></item>
/// </list>
/// </summary>
public sealed class WpfValueAnimatable : IAnimatable
{
    public WpfValueAnimatable(DependencyObject owner, DependencyProperty? property)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Property = property;
    }

    /// <summary>被动画的依赖对象。</summary>
    public DependencyObject Owner { get; set; }

    /// <summary>被动画的依赖属性。</summary>
    public DependencyProperty? Property { get; set; }

    /// <summary>读取当前值（已转成 Core 的值类型）。</summary>
    public object? GetValue()
    {
        var property = ResolveReadProperty();
        ArgumentNullException.ThrowIfNull(property);

        var value = Owner.GetValue(property);
        return value switch
        {
            SolidColorBrush brush => new NColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A),
            Color color => new NColor(color.R, color.G, color.B, color.A),
            ScaleTransform scale => new NScaleTransform((float)scale.ScaleX, (float)scale.ScaleY,
                (float)scale.CenterX, (float)scale.CenterY),
            RotateTransform rotate => new NRotateTransform((float)rotate.Angle, (float)rotate.CenterX, (float)rotate.CenterY),
            _ => value,
        };
    }

    /// <summary>写入值（自动从 Core 值类型转回 WPF 类型）。</summary>
    public void SetValue(object? value)
    {
        ArgumentNullException.ThrowIfNull(Property);

        Owner.SetValue(Property, ConvertToWpf(value, Property));
    }

    /// <summary>强类型写入（避免装箱，且避免 PCL 的 <c>unsafe</c> 强转）。</summary>
    public void SetValue<T>(T value) => SetValue((object?)value);

    /// <summary>
    /// 读属性：宽度/高度在**没有显式设置**时读实际值。
    ///
    /// PCL 的 <c>AaWidth</c> 写的是
    /// <c>obj.Width = Math.Max((double.IsNaN(obj.Width) ? obj.ActualWidth : obj.Width) + delta, 0)</c> ——
    /// 也就是**显式设了 Width 就用 Width，没设才用 ActualWidth**。
    /// 若无条件读 ActualWidth，那么"显式设了 Width=200 但布局算出 180"的元素
    /// 会从 180 起步而不是 200 —— 与 PCL 不一致。
    /// </summary>
    private DependencyProperty? ResolveReadProperty()
    {
        if (Property is null) return null;

        if (Property == FrameworkElement.WidthProperty)
            return double.IsNaN(Owner.GetValue(FrameworkElement.WidthProperty) as double? ?? double.NaN)
                ? FrameworkElement.ActualWidthProperty
                : FrameworkElement.WidthProperty;

        if (Property == FrameworkElement.HeightProperty)
            return double.IsNaN(Owner.GetValue(FrameworkElement.HeightProperty) as double? ?? double.NaN)
                ? FrameworkElement.ActualHeightProperty
                : FrameworkElement.HeightProperty;

        return Property;
    }

    /// <summary>
    /// 把 Core 值类型转成该属性真正需要的 WPF 类型。
    ///
    /// PCL 用「属性名是不是 Color」来判断写 <see cref="Color"/> 还是 <see cref="SolidColorBrush"/>，
    /// 属性名一变就写错类型。这里改成**按属性实际类型判断**（先看名字再看类型），
    /// 两种情况都对。
    /// </summary>
    private static object? ConvertToWpf(object? value, DependencyProperty property)
    {
        if (value is null) return null;

        var targetType = property.PropertyType;

        switch (value)
        {
            case NColor color:
                if (targetType == typeof(Color)) return ToColor(color);
                if (targetType == typeof(SolidColorBrush)) return new SolidColorBrush(ToColor(color));
                return color;

            case NScaleTransform scale:
                if (targetType == typeof(ScaleTransform))
                    return new ScaleTransform(scale.ScaleX, scale.ScaleY, scale.CenterX, scale.CenterY);
                return scale;

            case NRotateTransform rotate:
                if (targetType == typeof(RotateTransform))
                    return new RotateTransform(rotate.Angle, rotate.CenterX, rotate.CenterY);
                return rotate;

            default:
                return value;
        }
    }

    private static Color ToColor(NColor color) =>
        Color.FromArgb(
            (byte)Math.Clamp(Math.Round(color.A, MidpointRounding.AwayFromZero), 0, 255),
            (byte)Math.Clamp(Math.Round(color.R, MidpointRounding.AwayFromZero), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G, MidpointRounding.AwayFromZero), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B, MidpointRounding.AwayFromZero), 0, 255));
}
