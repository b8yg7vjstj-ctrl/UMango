namespace UMango.Core.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// 一条日志。
///
/// 记下 Category 是为了让日志"能按来源过滤"：
/// 排查包源问题时只想看 PackageManagers 的日志，
/// 而不是在混在一起的几千行里翻找。
/// </summary>
public sealed record LogEntry(
    DateTimeOffset At,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception)
{
    /// <summary>单行文本形式（文件输出与界面显示共用同一种格式，便于对照）</summary>
    public string ToLine()
    {
        var head = $"[{At:yyyy-MM-dd HH:mm:ss.fff}] [{LevelText}] [{Category}] {Message}";
        return Exception is null ? head : head + Environment.NewLine + Exception;
    }

    /// <summary>级别短名（定长，便于日志里纵向对齐）</summary>
    public string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR"
    };
}
