// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的「主题 token 名 → 画刷」转换器。
//
// 与 WPF 侧 `src/UMango.App/Services/ThemeBrushKeyConverter.cs` **同构**：
// `DiagnosticSourceRow.StateBrushKey` 在 Shell 层（平台中立）只能给出主题 key
// （如 "BrC3"），由各壳用自己的资源查找解析成画刷 —— 这样颜色仍然随主题
// （浅色/深色、PCL/Fluent）变化，而不是在 Shell 里写死一个 Brush
// （那会让 Shell 依赖某个 UI 框架的类型，Avalonia 壳就用不了了）。

using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UMango.Avalonia.Converters;

/// <summary>把「主题 token 名」（如 <c>"BrC3"</c>）解析成当前主题下的画刷。</summary>
public sealed class ThemeBrushKeyConverter : IValueConverter
{
    public static ThemeBrushKeyConverter Instance { get; } = new();

    /// <summary>解析不到 key 时的兜底颜色（中性灰，任何主题下都可读）。</summary>
    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#8C8C8C"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return Fallback;

        try
        {
            // 主题变体传 null = 用当前生效的变体（RequestedThemeVariant / 系统跟随）。
            if (Application.Current is { } app
                && app.TryGetResource(key, null, out var found)
                && found is IBrush brush)
            {
                return brush;
            }
        }
        catch (Exception)
        {
            // 无应用上下文（无头测试/设计器）时资源查找可能抛；
            // 这是绑定求值路径，抛出去会让整行渲染失败，退回落色更安全。
        }

        return Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("主题画刷是单向绑定");
}
