using System.IO.Compression;
using UMango.App.ViewModels;
using UMango.Core.Logging;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 日志页测试。
///
/// 重点是**导出诊断包**：真机出问题时，开发侧唯一能拿到的就是这一份 zip，
/// 所以"包里到底有没有东西、内容对不对"必须被验证，
/// 而不是等用户发来一个空包才发现。
/// </summary>
public class LogViewModelTests
{
    private static MemoryLogSink SinkWith(params (LogLevel Level, string Message)[] entries)
    {
        var sink = new MemoryLogSink(capacity: 500);
        foreach (var (level, message) in entries)
            sink.Write(new LogEntry(DateTimeOffset.Now, level, "T", message, null));
        return sink;
    }

    [Fact]
    public void 空日志时状态文案不为空()
    {
        var vm = new LogViewModel(new MemoryLogSink());
        Assert.Empty(vm.Entries);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void 内存sink为null时也不崩()
    {
        // AppServices.Logs 未初始化时（例如设计器里）也要能用
        var vm = new LogViewModel(new MemoryLogSink());
        vm.Reload();
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void 日志按最新在前排列()
    {
        var sink = SinkWith((LogLevel.Info, "第一条"), (LogLevel.Info, "第二条"), (LogLevel.Info, "第三条"));
        var vm = new LogViewModel(sink);

        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal("第三条", vm.Entries[0].Message);   // 最新的在最上面
        Assert.Equal("第一条", vm.Entries[2].Message);
    }

    [Fact]
    public void 按级别过滤()
    {
        var sink = SinkWith(
            (LogLevel.Debug, "调试"),
            (LogLevel.Info, "信息"),
            (LogLevel.Warn, "警告"),
            (LogLevel.Error, "错误"));

        var vm = new LogViewModel(sink);
        Assert.Equal(4, vm.Entries.Count);

        vm.SelectedLevel = vm.Levels.First(l => l.Level == LogLevel.Warn);
        Assert.Equal(2, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.True(e.Level >= LogLevel.Warn));

        vm.SelectedLevel = vm.Levels.First(l => l.Level == LogLevel.Error);
        Assert.Single(vm.Entries);
        Assert.Equal("错误", vm.Entries[0].Message);
    }

    [Fact]
    public void 新日志实时插到最前面()
    {
        var sink = SinkWith((LogLevel.Info, "旧的"));
        var vm = new LogViewModel(sink);

        sink.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Error, "T", "新的", null));

        Assert.Equal("新的", vm.Entries[0].Message);
        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void 新日志若不符合当前过滤条件则不插入()
    {
        var sink = SinkWith((LogLevel.Error, "错误一"));
        var vm = new LogViewModel(sink);
        vm.SelectedLevel = vm.Levels.First(l => l.Level == LogLevel.Error);
        var before = vm.Entries.Count;

        sink.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Debug, "T", "调试噪音", null));

        Assert.Equal(before, vm.Entries.Count);
    }

    [Fact]
    public void 清空只清内存日志()
    {
        var sink = SinkWith((LogLevel.Info, "a"), (LogLevel.Info, "b"));
        var vm = new LogViewModel(sink);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Entries);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void 丢弃条数会被如实告知()
    {
        var sink = new MemoryLogSink(capacity: 50);
        for (var i = 0; i < 80; i++)
            sink.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "T", $"第 {i} 条", null));

        var vm = new LogViewModel(sink);

        Assert.True(vm.HasMore);
        Assert.Contains("已丢弃", vm.StatusMessage);
    }

    // ================= 导出诊断包 =================

    [Fact]
    public void 导出诊断包会生成含日志与摘要的zip()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"umango-diag-{Guid.NewGuid():N}");
        try
        {
            var sink = SinkWith((LogLevel.Info, "启动正常"), (LogLevel.Error, "某处出错了"));
            var vm = new LogViewModel(sink);

            var zipPath = vm.ExportDiagnosticBundle(outDir);

            Assert.NotNull(zipPath);
            Assert.True(File.Exists(zipPath));

            using var zip = ZipFile.OpenRead(zipPath!);
            var names = zip.Entries.Select(e => e.FullName).ToList();

            // 摘要与内存日志必须在包里 —— 这是最重要的一份
            Assert.Contains(names, n => n.Contains("摘要"));

            var summary = ReadEntry(zip, names.First(n => n.Contains("摘要")));
            Assert.Contains("启动正常", summary);
            Assert.Contains("某处出错了", summary);     // 错误必须带上，否则包就失去意义
            Assert.Contains("平台", summary);

            Assert.Null(vm.ErrorMessage);
            Assert.Contains("诊断包已导出", vm.StatusMessage);
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 导出文件名带时间戳避免覆盖()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"umango-diag-{Guid.NewGuid():N}");
        try
        {
            var vm = new LogViewModel(new MemoryLogSink());
            var first = vm.ExportDiagnosticBundle(outDir);
            Thread.Sleep(1100);   // 文件名精确到秒，跨一秒才是两次不同的导出

            var second = vm.ExportDiagnosticBundle(outDir);

            Assert.NotEqual(first, second);
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 导出失败时给出可读错误而不是抛异常()
    {
        var sink = SinkWith((LogLevel.Info, "x"));
        var vm = new LogViewModel(sink);

        // 用"文件当目录"制造失败
        var root = Path.Combine(Path.GetTempPath(), $"umango-blk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker.txt");
        File.WriteAllText(blocker, "我是文件");

        try
        {
            var result = vm.ExportDiagnosticBundle(Path.Combine(blocker, "sub"));
            Assert.Null(result);
            Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
