// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UMango.Shell.ViewModels;
using UMango.Core.Download;
using UMango.Core.Localization;
using UMango.Core.Terminal;
using UMango.Core.Tools;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 工具页动作（垃圾清理 / 自定义下载）的测试。
///
/// 关键约束：这些用例**不联网**、**不碰真实用户目录**——
/// 清理目标由构造函数注入到临时目录；下载用假引擎。
/// </summary>
public sealed class ToolsActionsViewModelTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- 垃圾清理 ----------

    [Fact]
    public void 构造时_统计出可清理内容()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");

            var vm = new ToolsActionsViewModel(
                new DownloadEngineRegistry([]),
                () => new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir });

            Assert.Single(vm.CleanupItems);
            // 摘要走本地化；无界面环境下 Provider 只回键本身（不抛），所以这里断言"有内容"
            // 而不是断言具体数字 —— 数字的准确性由 CleanupItems[0].FileCount 负责。
            Assert.Equal(1, vm.CleanupItems[0].FileCount);
            Assert.Equal(5, vm.CleanupItems[0].TotalBytes);
            Assert.False(string.IsNullOrWhiteSpace(vm.CleanupSummary));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 构造时_空目录显示没有可清理内容()
    {
        var dir = MakeTempDir();
        try
        {
            var vm = new ToolsActionsViewModel(
                new DownloadEngineRegistry([]),
                () => new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir });

            Assert.Single(vm.CleanupItems);
            Assert.Equal(0, vm.CleanupItems[0].FileCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 清理_删掉文件并更新统计()
    {
        var dir = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "123");

            var vm = new ToolsActionsViewModel(
                new DownloadEngineRegistry([]),
                () => new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir });

            await vm.CleanupCommand.ExecuteAsync(null);

            Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            Assert.Equal(0, vm.CleanupItems[0].FileCount);
            Assert.NotNull(vm.StatusMessage);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 清理_进行中标志会复位()
    {
        var dir = MakeTempDir();
        try
        {
            var vm = new ToolsActionsViewModel(
                new DownloadEngineRegistry([]),
                () => new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir });

            await vm.CleanupCommand.ExecuteAsync(null);

            Assert.False(vm.IsCleaning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 重新统计_命令可用()
    {
        var dir = MakeTempDir();
        try
        {
            var vm = new ToolsActionsViewModel(
                new DownloadEngineRegistry([]),
                () => new Dictionary<CleanupTarget, string> { [CleanupTarget.Cache] = dir });

            vm.RefreshCleanupSummaryCommand.Execute(null);
            Assert.Single(vm.CleanupItems);
            Assert.Equal(CleanupTarget.Cache, vm.CleanupItems[0].Target);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 自定义下载 ----------

    [Fact]
    public async Task 自定义下载_非法URL_报错且不发请求()
    {
        var engine = new FakeEngine { ShouldSucceed = true };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "not a url";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        // 断言**具体**的错误键，而不是"有错误就行"：
        // 绕过校验后代码仍会因为别的原因失败（比如把 null URL 丢给引擎抛异常），
        // 那样"ErrorMessage 非空"照样成立，测试就咬不住真正的缺陷。
        Assert.Equal(LocalizationService.Text("Validation.InvalidUrl"), vm.ErrorMessage);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task 自定义下载_空URL_报错()
    {
        var engine = new FakeEngine { ShouldSucceed = true };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task 自定义下载_成功_显示完成路径()
    {
        var engine = new FakeEngine { ShouldSucceed = true, OutputPath = "/tmp/out/file.zip" };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "https://example.com/a.zip";
        vm.CustomDownloadFileName = "a.zip";
        vm.CustomDownloadDirectory = "/tmp/out";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.StatusMessage);
        Assert.Equal(1, engine.CallCount);
        Assert.Equal("a.zip", engine.LastRequest?.OutputFileName);
    }

    [Fact]
    public async Task 自定义下载_引擎失败_显示失败原因()
    {
        var engine = new FakeEngine { ShouldSucceed = false, FailureMessage = "连接被拒绝" };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "https://example.com/a.zip";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task 自定义下载_非法文件名_被规范化而不是直接拒绝()
    {
        var engine = new FakeEngine { ShouldSucceed = true };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "https://example.com/a.zip";
        vm.CustomDownloadFileName = @"C:\temp\x\file.bin";
        vm.CustomDownloadDirectory = "/tmp/out";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.CallCount);
        Assert.Equal("file.bin", engine.LastRequest?.OutputFileName);
    }

    [Fact]
    public async Task 自定义下载_进行中标志会复位()
    {
        var engine = new FakeEngine { ShouldSucceed = true };
        var vm = new ToolsActionsViewModel(
            new DownloadEngineRegistry([engine]),
            () => new Dictionary<CleanupTarget, string>());

        vm.CustomDownloadUrl = "https://example.com/a.zip";
        await vm.CustomDownloadCommand.ExecuteAsync(null);

        Assert.False(vm.IsDownloading);
    }

    private sealed class FakeEngine : IDownloadEngine
    {
        public bool ShouldSucceed { get; set; } = true;
        public string? FailureMessage { get; set; }
        public string? OutputPath { get; set; }
        public int CallCount { get; private set; }
        public DownloadRequest? LastRequest { get; private set; }

        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool IsAvailable => true;
        public string? ExecutablePath => null;
        public string LicenseNote => "测试用假引擎";
        public IReadOnlyList<DownloadProtocol> Protocols { get; } = [DownloadProtocol.Http, DownloadProtocol.Https];

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, TerminalBridge? bridge,
            Action<DownloadProgress>? onProgress, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            if (ShouldSucceed)
                return Task.FromResult(new DownloadResult(true, OutputPath, null, null, 1));
            return Task.FromResult(new DownloadResult(false, null, "failed", FailureMessage ?? "失败", 1));
        }
    }
}
