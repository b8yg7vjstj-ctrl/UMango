namespace UMango.Core.UI.Theme;

/// <summary>
/// 主题跟随策略。
/// </summary>
public enum ThemeFollowMode
{
    /// <summary>跟随系统（系统深色就用深色，系统浅色就用浅色）。</summary>
    System = 0,

    /// <summary>始终浅色。</summary>
    Light = 1,

    /// <summary>始终深色。</summary>
    Dark = 2,
}

/// <summary>
/// 系统主题探测结果。
/// </summary>
/// <param name="IsDark">系统当前是否处于深色模式。</param>
/// <param name="IsAvailable">是否能读到系统主题。</param>
/// <param name="Detail">诊断说明（读不到时的原因）。</param>
public readonly record struct SystemThemeState(bool IsDark, bool IsAvailable, string Detail)
{
    /// <summary>读不到系统主题时的默认状态：按浅色处理。</summary>
    public static SystemThemeState Unknown { get; } = new(false, false, "无法读取系统主题");

    /// <summary>已读到系统主题。</summary>
    public static SystemThemeState Known(bool isDark) => new(isDark, true, string.Empty);
}

/// <summary>
/// 把「跟随策略」与「系统探测结果」解析成最终要用的深浅色。
///
/// 这段逻辑放在 Core 是为了可测：探测系统主题在 macOS 上要跑外部命令、
/// 在 Windows 上要读注册表，都是有副作用的；而「策略 → 结果」的映射是纯逻辑。
/// </summary>
public static class SystemThemeResolver
{
    /// <summary>
    /// 解析最终深浅色。
    /// </summary>
    /// <param name="mode">跟随策略。</param>
    /// <param name="system">系统探测结果。</param>
    /// <param name="fallbackDark">系统主题读不到时使用的兜底（一般是用户上次手动选的）。</param>
    public static bool Resolve(ThemeFollowMode mode, SystemThemeState system, bool fallbackDark)
    {
        return mode switch
        {
            ThemeFollowMode.Dark => true,
            ThemeFollowMode.Light => false,
            ThemeFollowMode.System => system.IsAvailable ? system.IsDark : fallbackDark,
            _ => fallbackDark,
        };
    }

    /// <summary>把跟随策略映射成设置项里存的字符串。</summary>
    public static string ToStorageValue(ThemeFollowMode mode) => mode switch
    {
        ThemeFollowMode.Dark => "dark",
        ThemeFollowMode.Light => "light",
        _ => "system",
    };

    /// <summary>把设置项里存的字符串解析成跟随策略；不认识的值按「跟随系统」处理。</summary>
    public static ThemeFollowMode FromStorageValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" => ThemeFollowMode.Dark,
        "light" => ThemeFollowMode.Light,
        _ => ThemeFollowMode.System,
    };
}
