using System.Text;

namespace UMango.Core.Logging;

/// <summary>
/// 写文件的日志目标：按日期分文件，自动清理旧文件。
///
/// **为什么按日期分文件而不是单个大文件**：
/// 单个文件会无限增长，用户想发日志给你时不知道该发哪一段；
/// 按日期分之后"今天的那个文件"就是排查当前问题的全部内容，
/// 可以直接整份附上。这也是 PCL-CE 的做法（Launch-yyyy-M-d-N.log）。
///
/// **绝不抛异常**：磁盘满、无权限、路径被占用都只能静默降级（丢弃该条日志），
/// 绝不把异常抛回调用方 —— 日志系统不该成为新的故障源。
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _keepDays;
    private readonly Lock _gate = new();

    private StreamWriter? _writer;
    private DateOnly _currentDate;

    public FileLogSink(string directory, string prefix = "umango", int keepDays = 7)
    {
        _directory = directory;
        _prefix = prefix;
        _keepDays = Math.Max(1, keepDays);
    }

    /// <summary>已因写入失败而丢弃的条数（界面可据此提示"日志可能不完整"）</summary>
    public long FailedWriteCount { get; private set; }

    /// <summary>当前日志文件路径；尚未成功写过则为 null。</summary>
    public string? CurrentFilePath { get; private set; }

    public void Write(LogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                EnsureWriter(entry.At);
                _writer?.WriteLine(entry.ToLine());
                // 每行都刷：崩溃时最后几行往往正是关键，
                // 而包管理 GUI 的日志量不大（不是高频交易系统），这点代价值得。
                _writer?.Flush();
            }
        }
        catch (Exception)
        {
            FailedWriteCount++;
            // 写不进去就算了：关闭 writer 让下次重试，但绝不抛回调用方
            try { _writer?.Dispose(); } catch { /* 忽略 */ }
            _writer = null;
        }
    }

    public void Flush()
    {
        try
        {
            lock (_gate) _writer?.Flush();
        }
        catch (Exception)
        {
            FailedWriteCount++;
        }
    }

    /// <summary>把当前 writer 关掉并置空（进程退出、或用户要求导出时调用）。</summary>
    public void Close()
    {
        try
        {
            lock (_gate)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
        catch (Exception)
        {
            FailedWriteCount++;
        }
    }

    private void EnsureWriter(DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(at.LocalDateTime);
        if (_writer is not null && date == _currentDate) return;

        // 换日或首次：收掉旧的，开新的，顺手清理过期文件
        try { _writer?.Dispose(); } catch { /* 忽略 */ }
        _writer = null;

        Directory.CreateDirectory(_directory);
        _currentDate = date;

        var path = Path.Combine(_directory, $"{_prefix}-{date:yyyy-MM-dd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
        CurrentFilePath = path;

        CleanupOldFiles(date);
    }

    private void CleanupOldFiles(DateOnly today)
    {
        try
        {
            var cutoff = today.AddDays(-_keepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stamp = name.Length > _prefix.Length + 1 ? name[(_prefix.Length + 1)..] : null;
                if (stamp is null || !DateOnly.TryParse(stamp, out var d)) continue;
                if (d < cutoff) File.Delete(file);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响写日志
        }
    }

    /// <summary>列出已有的日志文件（新的在前），供"导出日志"用。</summary>
    public IReadOnlyList<string> ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(_directory)) return [];
            return Directory.EnumerateFiles(_directory, $"{_prefix}-*.log")
                            .OrderByDescending(f => f, StringComparer.Ordinal)
                            .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
