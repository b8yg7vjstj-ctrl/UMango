// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using UMango.Core.Diagnostics;
using Xunit;

namespace UMango.Core.Tests.Diagnostics;

public class CrashReportingTests
{
    [Fact]
    public void 报告工厂_填入异常基本信息()
    {
        Exception? captured = null;
        try
        {
            throw new InvalidOperationException("炸了");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var report = CrashReportFactory.Create(captured!);

        Assert.Equal("System.InvalidOperationException", report.ExceptionType);
        Assert.Equal("炸了", report.Message);
        Assert.NotNull(report.StackTrace);
        Assert.False(string.IsNullOrEmpty(report.Id));
        Assert.Equal(CrashSeverity.Error, report.Severity);
    }

    [Fact]
    public void 报告工厂_内层异常链被展开()
    {
        var ex = new InvalidOperationException("外",
            new ArgumentException("中", new FormatException("内")));

        var report = CrashReportFactory.Create(ex);

        Assert.Equal(2, report.InnerExceptions.Count);
        Assert.Contains("ArgumentException", report.InnerExceptions[0]);
        Assert.Contains("FormatException", report.InnerExceptions[1]);
    }

    [Fact]
    public void 报告工厂_自定义消息覆盖异常消息()
    {
        var report = CrashReportFactory.Create(new Exception("原始"), CrashSeverity.Warning, "自定义");
        Assert.Equal("自定义", report.Message);
    }

    [Fact]
    public void 报告工厂_环境快照被拷入()
    {
        var report = CrashReportFactory.Create(new Exception("x"), environment:
            new Dictionary<string, string> { ["os"] = "linux" });

        Assert.Equal("linux", report.Environment["os"]);
    }

    [Fact]
    public void 报告工厂_空异常抛参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => CrashReportFactory.Create(null!));
    }

    [Fact]
    public void 过滤器_默认可预期异常被丢弃()
    {
        Assert.True(CrashReportFilter.ShouldDrop(new TimeoutException()));
        Assert.True(CrashReportFilter.ShouldDrop(new HttpRequestException()));
        Assert.True(CrashReportFilter.ShouldDrop(new OperationCanceledException()));
        Assert.True(CrashReportFilter.ShouldDrop(new FileNotFoundException()));
        Assert.True(CrashReportFilter.ShouldDrop(new DirectoryNotFoundException()));
        Assert.True(CrashReportFilter.ShouldDrop(new UnauthorizedAccessException()));
    }

    [Fact]
    public void 过滤器_普通异常不被丢弃()
    {
        Assert.False(CrashReportFilter.ShouldDrop(new InvalidOperationException()));
    }

    [Fact]
    public void 过滤器_内层异常在排除清单里也算丢弃()
    {
        var ex = new InvalidOperationException("外", new TimeoutException("内"));
        Assert.True(CrashReportFilter.ShouldDrop(ex));
    }

    [Fact]
    public void 过滤器_可增删排除项()
    {
        var type = typeof(InvalidTimeZoneException);
        Assert.False(CrashReportFilter.ShouldDrop(new InvalidTimeZoneException()));
        CrashReportFilter.Exclude(type);
        try
        {
            Assert.True(CrashReportFilter.ShouldDrop(new InvalidTimeZoneException()));
        }
        finally
        {
            CrashReportFilter.Restore(type);
        }

        Assert.False(CrashReportFilter.ShouldDrop(new InvalidTimeZoneException()));
    }

    [Fact]
    public void 存储_写入后可列出()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CrashReportStore(dir);
            var report = CrashReportFactory.Create(new Exception("x"));
            var path = store.Save(report);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Single(store.List());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void 存储_超出保留份数时裁剪最旧的()
    {
        var dir = NewTempDir();
        try
        {
            // 先用宽松的保留数存 5 份，再显式裁剪 —— 避免 Save() 内部自动裁剪带来的时序干扰
            var writer = new CrashReportStore(dir, keepCount: 100);
            for (var i = 0; i < 5; i++)
            {
                var report = CrashReportFactory.Create(new Exception("x" + i));
                report.Id = "20240101-000000-000-" + i;
                writer.Save(report);
                File.SetLastWriteTimeUtc(Path.Combine(dir, $"crash-{report.Id}.json"),
                    DateTime.UtcNow.AddSeconds(i));
            }

            Assert.Equal(5, writer.List().Count);

            var store = new CrashReportStore(dir, keepCount: 2);
            Assert.Equal(3, store.Prune());
            Assert.Equal(2, store.List().Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void 存储_目录不存在时自动创建()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "sub", "crashes");
        try
        {
            var store = new CrashReportStore(dir);
            var path = store.Save(CrashReportFactory.Create(new Exception("x")));

            Assert.NotNull(path);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 存储_保留份数小于一按一处理()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CrashReportStore(dir, keepCount: 0);
            for (var i = 0; i < 3; i++)
            {
                var report = CrashReportFactory.Create(new Exception("x"));
                report.Id = "20240101-000000-000-" + i;
                store.Save(report);
            }

            Assert.Single(store.List());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void 存储_空目录路径抛异常()
    {
        Assert.Throws<ArgumentException>(() => new CrashReportStore("  "));
    }

    [Fact]
    public void 存储_写不进去时返回空而不是抛异常()
    {
        // 用一个「是文件的路径」当目录 → 创建目录必然失败
        var root = NewTempDir();
        var file = Path.Combine(root, "not-a-dir");
        File.WriteAllText(file, "x");
        try
        {
            var store = new CrashReportStore(file);
            Assert.Null(store.Save(CrashReportFactory.Create(new Exception("x"))));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 启动校验_全部通过时无致命失败()
    {
        var validator = new StartupValidator()
            .Add("A", () => new StartupCheckResult { Name = "A", Passed = true });

        var results = validator.Run();

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.False(StartupValidator.HasFatalFailure(results));
    }

    [Fact]
    public void 启动校验_单项抛异常只让该项失败()
    {
        var validator = new StartupValidator()
            .Add("坏", () => throw new InvalidOperationException("内部错误"))
            .Add("好", () => new StartupCheckResult { Name = "好", Passed = true });

        var results = validator.Run();

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Passed);
        Assert.Contains("InvalidOperationException", results[0].Message);
        Assert.True(results[1].Passed);
    }

    [Fact]
    public void 启动校验_致命项失败被识别()
    {
        var validator = new StartupValidator()
            .Add("致命", () => new StartupCheckResult { Name = "致命", Passed = false }, fatal: true);

        var results = validator.Run();

        Assert.True(StartupValidator.HasFatalFailure(results));
    }

    [Fact]
    public void 启动校验_非致命项失败不算致命()
    {
        var validator = new StartupValidator()
            .Add("普通", () => new StartupCheckResult { Name = "普通", Passed = false });

        Assert.False(StartupValidator.HasFatalFailure(validator.Run()));
    }

    [Fact]
    public void 启动校验_检查项返回空结果时判失败()
    {
        var validator = new StartupValidator()
            .Add("空", () => null!);

        var results = validator.Run();

        Assert.False(results[0].Passed);
    }

    [Fact]
    public void 启动校验_登记空名字抛异常()
    {
        Assert.Throws<ArgumentException>(() => new StartupValidator().Add("  ", () => new StartupCheckResult()));
    }

    [Fact]
    public void 安装标识_不存在时生成并落盘()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "install-id.txt");
            var id = InstallIdentity.GetOrCreate(path);

            Assert.False(string.IsNullOrEmpty(id));
            Assert.True(File.Exists(path));
            Assert.Equal(id, File.ReadAllText(path));
        }
        finally
        {
            InstallIdentity.ResetCache();
            Cleanup(root);
        }
    }

    [Fact]
    public void 安装标识_已存在时复用()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "install-id.txt");
            File.WriteAllText(path, "fixed-id");
            InstallIdentity.ResetCache();

            Assert.Equal("fixed-id", InstallIdentity.GetOrCreate(path));
        }
        finally
        {
            InstallIdentity.ResetCache();
            Cleanup(root);
        }
    }

    [Fact]
    public void 安装标识_两次调用返回同一个值()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "install-id.txt");
            InstallIdentity.ResetCache();

            Assert.Equal(InstallIdentity.GetOrCreate(path), InstallIdentity.GetOrCreate(path));
        }
        finally
        {
            InstallIdentity.ResetCache();
            Cleanup(root);
        }
    }

    [Fact]
    public void 安装标识_不是机器指纹()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "install-id.txt");
            InstallIdentity.ResetCache();
            var id = InstallIdentity.GetOrCreate(path);

            // 随机 GUID 形态：32 位十六进制
            Assert.Matches("^[0-9a-f]{32}$", id);
        }
        finally
        {
            InstallIdentity.ResetCache();
            Cleanup(root);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }
}
