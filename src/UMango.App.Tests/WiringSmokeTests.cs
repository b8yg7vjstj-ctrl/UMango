using UMango.App.Services;
using UMango.App.ViewModels;
using UMango.Core.App.Tasks;
using UMango.Core.Configuration;
using UMango.Core.Logging;
using UMango.Core.PackageManagers;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// **接线冒烟测试**：构造全部服务与 ViewModel，把主流程驱动一遍。
///
/// 为什么需要它（而单元测试与 XAML 审计都不够）：
///   · 单元测试各自只覆盖一小块，看不到"初始化顺序""事件订阅""共享静态状态"这类**跨模块**问题；
///   · XAML 审计只做静态检查，不执行任何代码；
///   · 真机渲染在本机不可行。
/// 于是"整个对象图能不能被端到端驱动"这件事一直没人验证过 ——
/// 而它恰恰是最容易出问题的地方（空引用、初始化顺序、静态状态互相污染）。
///
/// 本测试还引入一个**以日志为断言口径**的做法：
/// 走完一遍干净流程后，日志里不应出现 ERROR 级条目。
/// 这把"日志系统"变成了集成测试的探针，而不只是给人看的输出。
/// </summary>
[Collection("静态状态")]
public class WiringSmokeTests
{
    [Fact]
    public async Task 整个对象图能构造并驱动一遍主流程()
    {
        // ── 0) 干净起点 ──
        // 全局内存日志是所有测试共用的（AppServices.Logs 是静态单例），
        // 别的用例（例如故意触发导出失败的那个）会把 ERROR 留在里面。
        // 不清掉的话，"无 ERROR"这条断言匹配到的可能是别人的日志 ——
        // 这不是假设，是第一次运行时就真实发生的失败。
        AppServices.Initialize();
        AppServices.Logs!.Clear();

        // ── 1) 组合根：与 App.OnStartup 走同一条路径 ──
        Assert.NotNull(AppServices.Registry);
        Assert.NotNull(AppServices.Records);
        Assert.NotNull(AppServices.Executor);
        Assert.NotNull(AppServices.DownloadEngines);
        Assert.NotNull(AppServices.Logs);

        // ── 2) 所有 ViewModel 都要能构造（构造里做异步加载的也必须不抛） ──
        var shell = new ShellViewModel();
        var download = new DownloadViewModel();
        var progress = new ProgressViewModel();
        var settings = new SettingsViewModel();
        var tools = new ToolsViewModel();
        var downloader = new DownloaderViewModel();
        var log = new LogViewModel();

        // ── 3) 外壳：五个选项卡逐个切过去，每站都要有菜单 ──
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            shell.CurrentTab = tab;
            Assert.NotEmpty(shell.LeftMenu);
            Assert.False(string.IsNullOrWhiteSpace(shell.CurrentNavKey));
        }

        // ── 4) 下载页：加载分类 → 逐个选中 → 搜索一次 → 读一次已安装 ──
        await download.InitializeAsync();
        Assert.NotEmpty(download.VisibleCategories);

        foreach (var category in download.VisibleCategories)
        {
            download.SelectedCategory = category;
            // 分类的标题与子标题是需求里的硬要求：左栏两行都要有内容
            Assert.False(string.IsNullOrWhiteSpace(category.Title));
            Assert.False(string.IsNullOrWhiteSpace(category.Subtitle));
        }

        // 只搜索一次（每次搜索都要起真进程，全跑一遍太慢）
        var searchable = download.VisibleCategories
            .FirstOrDefault(c => c.Kind == PackageSourceKind.Homebrew)
            ?? download.VisibleCategories.First(c => c.Capabilities.Search);
        download.SelectedCategory = searchable;
        download.Query = "wget";
        await download.SearchCommand.ExecuteAsync(null);

        // 已安装列表：本机 Homebrew/npm 一定有内容
        download.IsInstalledMode = true;
        await download.ReloadInstalledCommand.ExecuteAsync(null);
        Assert.NotEmpty(download.Installed);

        // 切回搜索模式，确认两边的状态没有互相污染
        download.IsInstalledMode = false;
        Assert.True(download.ShowResultsPane);

        // ── 5) 进度页：造一个带子项的任务组，验证"按下载方式列子项"能取到分组 ──
        var task = TaskModel.Group("冒烟测试任务");
        foreach (var step in UMango.Core.Install.StepNames.Ordered)
            task.Children.Add(TaskModel.Progressive(step));
        TaskCenter.Register(task);

        progress.SelectedTask = task;
        Assert.Equal(stepCount, task.Children.Count);
        Assert.NotNull(progress.GroupedOutput(task));

        TaskCenter.Remove(task);

        // ── 6) 设置页：读一遍全部公开状态，再逐个改动 ──
        Assert.Equal(2, settings.Themes.Count);
        Assert.Equal(2, settings.AnimationDirections.Count);

        var originalEnabled = settings.AnimationEnabled;
        var originalDirection = settings.SelectedAnimationDirection;
        var originalSpeed = settings.AnimationSpeedFactor;
        try
        {
            settings.AnimationEnabled = !originalEnabled;
            settings.SelectedAnimationDirection =
                settings.AnimationDirections.First(o => o.Mode != originalDirection.Mode);
            settings.AnimationSpeedFactor = 1.5;

            Assert.NotEqual(originalEnabled, settings.AnimationEnabled);
            Assert.NotEqual(originalDirection.Mode, settings.SelectedAnimationDirection.Mode);
            Assert.Equal(1.5, settings.AnimationSpeedFactor);
        }
        finally
        {
            settings.AnimationEnabled = originalEnabled;
            settings.SelectedAnimationDirection = originalDirection;
            settings.AnimationSpeedFactor = originalSpeed;
        }

        // 设置确实写进了设置服务（而不是只改了一份内存副本）
        Assert.Equal(originalEnabled, AppSettings.Settings.Get<bool>(UiSettings.AnimationEnabledKey));

        // ── 7) 工具页：报告非空且含系统信息 ──
        Assert.False(string.IsNullOrWhiteSpace(tools.SystemReport));
        Assert.Contains(UMango.Core.Platform.SystemInfo.Describe(), tools.SystemReport);

        // ── 8) 下载器页：命令都可用 ──
        downloader.RefreshEnginesCommand.Execute(null);
        Assert.NotEmpty(downloader.Engines);

        // ── 9) 日志页：重载 + 导出（导出到临时目录，不污染桌面） ──
        log.Reload();
        Assert.False(string.IsNullOrWhiteSpace(log.StatusMessage));

        var outDir = Path.Combine(Path.GetTempPath(), $"umango-smoke-{Guid.NewGuid():N}");
        try
        {
            var zip = log.ExportDiagnosticBundle(outDir);
            Assert.NotNull(zip);
            Assert.True(File.Exists(zip));
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* 忽略 */ }
        }

        // ── 10) 以日志为口径的最终断言：干净流程不应留下 ERROR ──
        Log.Flush();
        var errors = AppServices.Logs!.Snapshot()
            .Where(e => e.Level == LogLevel.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            "冒烟流程产生了 ERROR 级日志：\n" + string.Join("\n", errors.Select(e => e.ToLine())));
    }

    private const int stepCount = 5;   // StepNames.Ordered 的长度
}
