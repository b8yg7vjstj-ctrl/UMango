using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace UMango.App.Services;

/// <summary>
/// 把「主题 token 名」（如 "BrC3"）解析成当前主题下的画刷。
///
/// <para>
/// 为什么需要它：<c>DiagnosticSourceRow.StateBrushKey</c> 在 Shell 层（平台中立），
/// 不能直接返回 WPF 的 <see cref="Brush"/> —— 那会让 Shell 依赖 System.Windows.Media，
/// Avalonia 壳就用不了了。因此 Shell 只给出**主题 key**，各壳用自己的资源查找解析成画刷。
/// 好处是颜色仍然随主题（浅色/深色、PCL/Fluent）变化，而不是写死。
/// </para>
/// </summary>
public sealed class ThemeBrushKeyConverter : IValueConverter
{
    /// <summary>解析不到 key 时的兜底颜色（中性灰，任何主题下都可读）。</summary>
    private const string FallbackColor = "#8C8C8C";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) return FallbackBrush();

        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch (Exception)
        {
            // 无 WPF 应用上下文（无头测试/设计器）时 Application.Current 可能抛；
            // 这是绑定求值路径，抛出去会让整行渲染失败，退回落色更安全。
        }

        return FallbackBrush();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("主题画刷是单向绑定");

    private static Brush FallbackBrush()
    {
        var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(FallbackColor));
        solid.Freeze();
        return solid;
    }
}
