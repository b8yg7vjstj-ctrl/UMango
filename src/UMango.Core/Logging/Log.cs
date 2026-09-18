using System.Runtime.CompilerServices;

namespace UMango.Core.Logging;

/// <summary>
/// 日志门面（静态入口）。
///
/// **两条不可违背的设计约束**：
///   1. **任何日志调用都不得抛异常。** 每个公开方法整体包在 try/catch 里 ——
///      日志是排查问题的最后手段，它自己不能成为新故障源。
///   2. **不得阻塞调用方。** 日志会出现在 CLI 输出的热路径上（一次安装可能几万行），
///      这里只在锁内做入队/写入，不做任何等待外部资源的动作。
///
/// 未初始化时调用是安全的：消息进不落任何 sink，但也不报错 ——
/// 这样 Core 内部可以放心地到处记日志，不必担心"调用方还没初始化"。
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly List<ILogSink> Sinks = [];

    /// <summary>最低记录级别（低于它的直接丢弃，不产生任何开销）</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>是否已初始化</summary>
    public static bool IsInitialized
    {
        get { lock (Gate) return Sinks.Count > 0; }
    }

    /// <summary>因 sink 写入失败 / 未初始化而被丢弃的条数（诊断用）</summary>
    public static long DroppedCount { get; private set; }

    /// <summary>配置输出目标。可重复调用（会替换已有目标）。</summary>
    public static void Initialize(params ILogSink[] sinks)
    {
        try
        {
            lock (Gate)
            {
                Sinks.Clear();
                Sinks.AddRange(sinks);
            }
        }
        catch (Exception)
        {
            // 初始化失败也不能抛：宁可不记日志，也不能拦住应用启动
        }
    }

    /// <summary>取某个类型的 sink（例如界面要拿 MemoryLogSink 显示日志）。</summary>
    public static T? GetSink<T>() where T : class, ILogSink
    {
        try
        {
            lock (Gate) return Sinks.OfType<T>().FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message, null);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message, null);

    /// <summary>记录异常。会自动展开内部异常链 —— 只看最外层消息常常定位不到根因。</summary>
    public static void Error(string category, Exception exception, string? message = null)
        => Write(LogLevel.Error, category, message ?? exception.Message, Describe(exception));

    /// <summary>便捷写法：记录异常并附带调用点信息（便于定位是哪个方法抛的）。</summary>
    public static void ErrorAt(string category, Exception exception,
                              [CallerMemberName] string? member = null,
                              [CallerFilePath] string? file = null,
                              [CallerLineNumber] int line = 0)
    {
        var where = $"{Path.GetFileName(file ?? "")}:{line} {member}";
        Write(LogLevel.Error, category, $"{where} → {exception.Message}", Describe(exception));
    }

    /// <summary>把已配置的 sink 全部刷一遍（进程退出、崩溃前调用）。</summary>
    public static void Flush()
    {
        try
        {
            lock (Gate)
            {
                foreach (var sink in Sinks) sink.Flush();
            }
        }
        catch (Exception)
        {
            DroppedCount++;
        }
    }

    private static void Write(LogLevel level, string category, string message, string? exception)
    {
        // 级别过滤放在最前面：被过滤掉的消息不产生任何对象分配
        if (level < MinLevel) return;

        try
        {
            var entry = new LogEntry(DateTimeOffset.Now, level, category, message, exception);

            ILogSink[] snapshot;
            lock (Gate)
            {
                if (Sinks.Count == 0) { DroppedCount++; return; }
                snapshot = [.. Sinks];
            }

            foreach (var sink in snapshot)
            {
                try { sink.Write(entry); }
                catch (Exception)
                {
                    // 单个 sink 失败不影响其它 sink（文件写不进去时，界面里的日志仍应可见）
                    DroppedCount++;
                }
            }
        }
        catch (Exception)
        {
            DroppedCount++;
        }
    }

    /// <summary>展开异常链（含内部异常与 AggregateException 的子异常）。</summary>
    private static string Describe(Exception ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var current = ex;
            var depth = 0;

            while (current is not null && depth++ < 10)
            {
                if (depth > 1) sb.AppendLine().Append("── 内部异常：");
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                {
                    // 只留前若干帧：完整栈对用户来说太长，而定位通常只需要最上面几帧
                    var frames = current.StackTrace.Split('\n').Take(6);
                    sb.AppendLine();
                    sb.Append(string.Join(Environment.NewLine, frames));
                }

                if (current is AggregateException agg && agg.InnerExceptions.Count > 1)
                {
                    sb.AppendLine().Append($"── 聚合异常共 {agg.InnerExceptions.Count} 个：");
                    foreach (var inner in agg.InnerExceptions.Take(5))
                    {
                        sb.AppendLine().Append("   · ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                    }
                }

                current = current.InnerException;
            }
            return sb.ToString();
        }
        catch (Exception)
        {
            return ex.GetType().Name;
        }
    }
}
