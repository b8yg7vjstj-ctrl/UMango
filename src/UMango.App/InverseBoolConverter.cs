using System.Globalization;
using System.Windows.Data;

namespace UMango.App;

/// <summary>布尔取反。用于把 IsProgressKnown 取反后绑到 ProgressBar.IsIndeterminate。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
