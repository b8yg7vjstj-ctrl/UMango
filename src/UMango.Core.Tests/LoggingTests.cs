using UMango.Core.Logging;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 日志系统测试。
///
/// 重点不在"能不能记下来"，而在**两条不可违背的约束**：
///   1. 任何日志调用都不得抛异常（日志是排查手段，不能自己成为故障源）；
///   2. 单个 sink 失败不得影响其它 sink。
/// 这两条一旦破了，最坏情况是"为了记录失败而制造新的失败"。
/// </summary>
public class LoggingTests : IDisposable
{
    private readonly MemoryLogSink _memory = new(capacity: 100);

    public LoggingTests()
    {
        Log.Initialize(_memory);
        Log.MinLevel = LogLevel.Debug;
    }

    public void Dispose()
    {
        Log.Initialize();          // 清空，避免影响其它用例
        Log.MinLevel = LogLevel.Info;
    }

    // ================= 基本行为 =================

    [Fact]
    public void 能记下各级别日志()
    {
        Log.Debug("T", "d");
        Log.Info("T", "i");
        Log.Warn("T", "w");
        Log.Error("T", "e");

        var all = _memory.Snapshot();
        Assert.Equal(4, all.Count);
        Assert.Equal(LogLevel.Debug, all[0].Level);
        Assert.Equal(LogLevel.Error, all[3].Level);
    }

    [Fact]
    public void 日志行包含时间级别类别与内容()
    {
        Log.Info("PackageManagers", "探测 npm");
        var line = _memory.Snapshot()[0].ToLine();

        Assert.Contains("[INF]", line);
        Assert.Contains("[PackageManagers]", line);
        Assert.Contains("探测 npm", line);
        // 时间精确到毫秒：排查时序问题时秒级不够用
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", line);
    }

    [Fact]
    public void 低于最低级别的日志不产生任何记录()
    {
        Log.MinLevel = LogLevel.Warn;
        Log.Debug("T", "不该出现");
        Log.Info("T", "也不该出现");
        Log.Warn("T", "该出现");

        var all = _memory.Snapshot();
        Assert.Single(all);
        Assert.Equal("该出现", all[0].Message);
    }

    [Fact]
    public void 异常记录会展开内部异常链()
    {
        var inner = new InvalidOperationException("最内层原因");
        var outer = new ApplicationException("外层包装", inner);
        Log.Error("T", outer);

        var entry = _memory.Snapshot()[0];
        Assert.Contains("外层包装", entry.Exception);
        // 只看最外层消息常常定位不到根因，所以内部异常必须展开
        Assert.Contains("最内层原因", entry.Exception);
        Assert.Contains("InvalidOperationException", entry.Exception);
    }

    // ================= 内存 sink =================

    [Fact]
    public void 内存sink按容量裁剪并记录丢弃条数()
    {
        for (var i = 0; i < 150; i++) Log.Info("T", $"第 {i} 条");

        var all = _memory.Snapshot();
        Assert.Equal(100, all.Count);              // 容量上限生效
        Assert.Equal("第 50 条", all[0].Message);   // 保留的是最新的
        Assert.Equal(50, _memory.DroppedCount);     // 丢弃条数如实记录
    }

    [Fact]
    public void 新增日志会触发事件()
    {
        var seen = new List<string>();
        _memory.Appended += e => seen.Add(e.Message);
        Log.Info("T", "a");
        Log.Info("T", "b");
        Assert.Equal(["a", "b"], seen);
    }

    // ================= ★ 不可违背的约束 =================

    private sealed class ThrowingSink : ILogSink
    {
        public int Attempts { get; private set; }
        public void Write(LogEntry entry) { Attempts++; throw new IOException("磁盘满了"); }
        public void Flush() => throw new IOException("刷盘失败");
    }

    [Fact]
    public void sink抛异常时日志调用本身不得抛()
    {
        Log.Initialize(new ThrowingSink());
        // 这里若抛，日志就成了新的故障源。整个调用必须安全返回。
        var ex = Record.Exception(() =>
        {
            Log.Info("T", "x");
            Log.Error("T", "y");
            Log.Flush();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void 单个sink失败不影响其它sink()
    {
        Log.Initialize(new ThrowingSink(), _memory);
        Log.Info("T", "仍然应该进内存");

        // 文件/磁盘出问题时，界面里的日志仍应可见 —— 这是第一手排查材料
        Assert.Contains(_memory.Snapshot(), e => e.Message == "仍然应该进内存");
    }

    [Fact]
    public void 未初始化时调用日志是安全的()
    {
        Log.Initialize();   // 没有 sink
        var ex = Record.Exception(() => Log.Info("T", "无处可去"));
        Assert.Null(ex);
    }

    [Fact]
    public void 空类别或空消息不会导致异常()
    {
        var ex = Record.Exception(() =>
        {
            Log.Info("", "");
            Log.Error("", new Exception("x"));
        });
        Assert.Null(ex);
    }

    [Fact]
    public void 能取回已注册的sink()
    {
        Assert.Same(_memory, Log.GetSink<MemoryLogSink>());
    }

    // ================= 文件 sink =================

    [Fact]
    public void 文件sink写盘_按日期命名_可读回()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"umango-log-{Guid.NewGuid():N}");
        try
        {
            var file = new FileLogSink(dir);
            file.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "T", "写盘测试", null));
            file.Close();

            Assert.NotNull(file.CurrentFilePath);
            Assert.True(File.Exists(file.CurrentFilePath));
            Assert.Contains("umango-", Path.GetFileName(file.CurrentFilePath!));
            Assert.EndsWith(".log", file.CurrentFilePath!);
            Assert.Contains("写盘测试", File.ReadAllText(file.CurrentFilePath!));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 文件sink在无法写入的路径上静默降级()
    {
        // 造一个**真正**不可写的路径：先建一个普通文件，再把它当目录用。
        // （第一版用了多层不存在的目录，但 Directory.CreateDirectory 会递归创建中间目录，
        //   于是"写不进去"根本没发生、断言落空 —— 是测试的假设错了，不是代码错了。）
        var root = Path.Combine(Path.GetTempPath(), $"umango-blocker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker.txt");
        File.WriteAllText(blocker, "我是文件，不是目录");

        var bogus = Path.Combine(blocker, "sub");
        var file = new FileLogSink(bogus);

        var ex = Record.Exception(() =>
        {
            file.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "T", "写不进去", null));
            file.Flush();
            file.Close();
        });

        Assert.Null(ex);                    // 绝不抛
        Assert.True(file.FailedWriteCount > 0);   // 但要如实计数，便于界面提示"日志可能不完整"

        try { Directory.Delete(root, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void 文件sink会清理超过保留期的旧日志()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"umango-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // 造一个 30 天前的日志文件（保留期 7 天）
            var old = Path.Combine(dir, $"umango-{DateOnly.FromDateTime(DateTime.Today.AddDays(-30)):yyyy-MM-dd}.log");
            File.WriteAllText(old, "旧日志");

            var file = new FileLogSink(dir, keepDays: 7);
            file.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "T", "触发一次写入", null));
            file.Close();

            Assert.False(File.Exists(old), "超过保留期的日志应被清理");
            Assert.True(file.ListLogFiles().Count >= 1);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 文件sink能列出已有日志且新的在前()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"umango-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "umango-2026-01-01.log"), "a");
            File.WriteAllText(Path.Combine(dir, "umango-2026-03-01.log"), "b");
            var file = new FileLogSink(dir);

            var list = file.ListLogFiles();
            Assert.Equal(2, list.Count);
            Assert.Contains("2026-03-01", list[0]);   // 新的在前
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }
}
