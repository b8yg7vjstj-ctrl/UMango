// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的通用值转换器 —— 与 WPF 侧（`src/UMango.App/BoolToVisibilityConverter.cs` 等）同构。
//
// 为什么放在 Avalonia 侧而不是 Shell：
//   转换器是**纯显示层**的东西（WPF 的 `Visibility` / Avalonia 的 `bool IsVisible`），
//   放 Shell 会让 Shell 依赖某个 UI 框架的类型 → 破坏"Shell 平台中立"的约定。
//   因此每个壳各有一份薄薄的适配，语义保持一致。

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UMango.Avalonia.Converters;

/// <summary>
/// 非空 → 可见（真=可见）。
///
/// <para>
/// WPF 侧对应 <c>NotEmptyToVis</c>。**Avalonia 侧的 <c>IsVisible</c> 是 bool，
/// 没有 "Collapsed" 这个第三态**，所以"空值"必须显式映射成 false。
/// </para>
///
/// <para>
/// 与 WPF 侧同构：**同时支持字符串与集合** —— 只判字符串的话，
/// 绑到集合（例如同一 formula 的其它版本列表）时会永远返回 false，
/// 表现为"那段信息静默不显示"，既不报错也看不出来。
/// </para>
/// </summary>
public sealed class NotEmptyToBoolConverter : IValueConverter
{
    public static NotEmptyToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            System.Collections.ICollection collection => collection.Count > 0,
            _ => true,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>空集合 / null → 可见（用于"一个包源都没探测到"这类空态）。</summary>
public sealed class EmptyCollectionToBoolConverter : IValueConverter
{
    public static EmptyCollectionToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return true;

        if (value is System.Collections.ICollection collection) return collection.Count == 0;

        // IEnumerable 但不是 ICollection（少见）：枚举一次取个数。
        if (value is System.Collections.IEnumerable enumerable)
        {
            var count = 0;
            foreach (var _ in enumerable)
            {
                count++;
                if (count > 0) return false;
            }
            return true;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔 → 可见（真=可见）。Avalonia 的 <c>IsVisible</c> 本身就是 bool，直接透传。</summary>
public sealed class BoolToBoolConverter : IValueConverter
{
    public static BoolToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 非空 → 取反可见（真=隐藏）。
///
/// <para>
/// WPF 侧对应 <c>BoolToVisInvert</c>。用来做"没有配置源时才显示提示"这类反向条件。
/// </para>
/// </summary>
public sealed class InverseNotEmptyToBoolConverter : IValueConverter
{
    public static InverseNotEmptyToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            bool b => !b,
            System.Collections.ICollection collection => collection.Count == 0,
            _ => false,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static InverseBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>时间 → <c>HH:mm:ss.fff</c>（日志页时间戳）。</summary>
public sealed class TimeToStringConverter : IValueConverter
{
    public static TimeToStringConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 日志时间戳是 DateTimeOffset（Core 的 LogEntry.At），
        // 本地时间显示即可 —— 排查问题看的是"本机当时几点"。
        return value switch
        {
            DateTimeOffset offset => offset.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTime time => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串 → <c>[值]</c>（日志页的分类前缀）。</summary>
public sealed class BracketConverter : IValueConverter
{
    public static BracketConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? string.Empty : $"[{value}]";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>0~1 的比例 → 百分比文本（进度页）。</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public static FractionToPercentConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double fraction) return string.Empty;

        var percent = Math.Clamp(fraction, 0d, 1d) * 100d;
        return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>取包名首字母（下载页卡片方块里的缩写）。</summary>
public sealed class PackageInitialConverter : IValueConverter
{
    public static PackageInitialConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return "?";

        var trimmed = text.Trim();
        return trimmed[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>多个字符串 → 逗号连接（已安装列表的"其它版本"）。</summary>
public sealed class JoinToTextConverter : IValueConverter
{
    public static JoinToTextConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Collections.IEnumerable enumerable) return string.Empty;

        var parts = new List<string>();
        foreach (var item in enumerable)
        {
            if (item is null) continue;
            parts.Add(item.ToString() ?? string.Empty);
        }

        return string.Join(", ", parts);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 日志级别 → 前景画笔。
///
/// <para>
/// WPF 侧用 <c>DataTrigger</c>（按 <c>Level</c> 的值换色）；Avalonia 没有 DataTrigger，
/// 于是改成「绑定一个返回画笔的属性」—— 语义等价，两边看到的是同一套配色。
/// </para>
///
/// <para>
/// 配色沿用 WPF 版：普通 = BrG3，警告 = BrWarnAmber，错误 = BrRed。
/// </para>
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public static LogLevelBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not UMango.Core.Logging.LogLevel level) return DefaultBrush();

        return level switch
        {
            UMango.Core.Logging.LogLevel.Warn => BrushFor("BrWarnAmber"),
            UMango.Core.Logging.LogLevel.Error => BrushFor("BrRed"),
            _ => DefaultBrush(),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static object DefaultBrush() => BrushFor("BrG3");

    /// <summary>
    /// 按资源键取画笔。
    ///
    /// <para>
    /// 找不到时退回一个不透明的中性灰，而不是抛异常 —— 颜色只是显示层的小事，
    /// 不该让整个日志页因为一个 token 缺失就崩掉。
    /// </para>
    /// </summary>
    private static object BrushFor(string key)
    {
        // 注意全名：当前命名空间是 UMango.Avalonia.Converters，
        // 裸写 `Application` 会先被解析成 UMango.Avalonia 命名空间 → CS0234。
        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, null, out var found) && found is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
    }
}

/// <summary>
/// 引擎「可用 / 不可用」小圆点：可用为主题蓝，不可用为红。
///
/// <para>
/// WPF 版用 DataTrigger 切画刷；Avalonia 没有 DataTrigger，
/// 所以这里把「状态 → 画刷」搬进转换器。两个壳的判定口径一致（同一个
/// <c>IsAvailable</c> 字段），只是实现位置不同。
/// </para>
/// </summary>
public sealed class AvailableDotConverter : IValueConverter
{
    public static AvailableDotConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? BrushFor("BrC3") : BrushFor("BrRed");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush BrushFor(string key)
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, null, out var found) && found is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
    }
}

/// <summary>
/// 可空 double → double：<c>ProgressFraction</c> 是 <c>double?</c>（拿不到进度时为 null），
/// 而 <c>ProgressBar.Value</c> 只接受 double。
///
/// <para>
/// null 时给 0（配合 <c>IsIndeterminate</c> 走不确定态，不显示假百分比）。
/// </para>
/// </summary>
public sealed class NullableDoubleConverter : IValueConverter
{
    public static NullableDoubleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 终端输出行是否走 stderr：true → 红色，false → 正常前景色。
///
/// <para>
/// WPF 版用 DataTrigger 绑 <c>IsStdErr</c>；Avalonia 侧同样只能靠转换器。
/// </para>
/// </summary>
public sealed class StdErrBrushConverter : IValueConverter
{
    public static StdErrBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = global::Avalonia.Application.Current;
        var key = value is true ? "BrRed" : "BrC1";

        if (app is not null && app.TryGetResource(key, null, out var found) && found is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 任务状态 → 状态图标（○ / ● / √ / × / –）。
///
/// <para>
/// 与 WPF 版 <c>ChildStepTemplate</c> 的 <c>StateIcon</c> 一一对应：
/// 等待 ○、进行中 ●、成功 √、失败 ×、取消 –。
/// </para>
/// </summary>
public sealed class TaskStateIconConverter : IValueConverter
{
    public static TaskStateIconConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is UMango.Core.App.Tasks.TaskState state
            ? state switch
            {
                UMango.Core.App.Tasks.TaskState.Running => "●",
                UMango.Core.App.Tasks.TaskState.Success => "√",
                UMango.Core.App.Tasks.TaskState.Failed => "×",
                UMango.Core.App.Tasks.TaskState.Canceled => "–",
                _ => "○",
            }
            : "○";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 任务状态 → 图标颜色。
///
/// <para>
/// WPF 版用 DataTrigger 逐状态换 Foreground；Avalonia 没有 DataTrigger，
/// 所以这里承担同样的职责。等待=灰、进行中=主题蓝、成功=成功绿、失败=红、取消=灰。
/// </para>
/// </summary>
public sealed class TaskStateBrushConverter : IValueConverter
{
    public static TaskStateBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is UMango.Core.App.Tasks.TaskState state
            ? state switch
            {
                UMango.Core.App.Tasks.TaskState.Running => "BrC3",
                UMango.Core.App.Tasks.TaskState.Success => "BrProgressSuccess",
                UMango.Core.App.Tasks.TaskState.Failed => "BrRed",
                _ => "BrG4",
            }
            : "BrG4";

        var app = global::Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, null, out var found) && found is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
