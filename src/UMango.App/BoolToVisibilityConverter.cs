using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UMango.App;

/// <summary>布尔值转 Visibility（true 显示）。用于按状态显隐进度条与子项区。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
