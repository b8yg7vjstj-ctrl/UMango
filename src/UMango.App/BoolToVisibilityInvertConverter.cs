using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UMango.App;

/// <summary>
/// 布尔值转 Visibility，<b>true 反转为隐藏</b>（<c>ConverterParameter=invert</c>）。
///
/// 为什么单独一个转换器，而不是给 <see cref="BoolToVisibilityConverter"/> 加参数分支：
/// 那个转换器被进度条等处大量使用，给它加分支会让"参数写错"变成一个**静默**的
/// 全局风险（所有绑定点都变反向）。独立一个键，误用只影响这一个地方。
/// </summary>
public sealed class BoolToVisibilityInvertConverter : IValueConverter
{
    /// <summary>转换：true → Collapsed，false → Visible。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>回转。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}
