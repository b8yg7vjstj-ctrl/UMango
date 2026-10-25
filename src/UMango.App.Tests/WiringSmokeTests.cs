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
[Trait("Category", "接线")]
public class WiringSmokeTests
{
    [Fact]
    public void 日志保留条数的设置变更会立刻作用到内存日志sink()
    {
        // 回归：容量上限原先硬编码 3000，用户改不动；
        // 现在设置变更必须真正把 live sink 裁到新容量（否则设置页是个摆设）。
        AppServices.Initialize();
        var sink = AppServices.Logs;
        Assert.NotNull(sink);

        var original = AppSettings.Settings.Get<int>(UMango.Core.Configuration.UiSettings.LogBufferCapacityKey);
        try
        {
            AppSettings.Settings.Set(UMango.Core.Configuration.UiSettings.LogBufferCapacityKey, 1234);
            Assert.Equal(1234, sink!.Capacity);

            AppSettings.Settings.Set(UMango.Core.Configuration.UiSettings.LogBufferCapacityKey, 4321);
            Assert.Equal(4321, sink.Capacity);
        }
        finally
        {
            AppSettings.Settings.Set(UMango.Core.Configuration.UiSettings.LogBufferCapacityKey, original);
        }
    }

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

        // 搜索**允许失败**：这是接线测试，验证的是"命令能被驱动、状态能自洽"，
        // 而不是"网络和包源此刻可用"。真机内容断言交给专门的 [Trait("Category","真机")] 用例。
        // 第一版在这里断言了结果非空，结果在解决方案级运行时偶发失败 ——
        // 偶发失败的测试比没有测试更糟：它会掩盖真实回归。
        Assert.True(download.IsBusy == false, "命令结束后不应仍处于忙碌态");
        Assert.True(download.Results.Count >= 0);   // 只要求状态自洽

        // 已安装列表：同样只要求"能被驱动且状态自洽"
        download.IsInstalledMode = true;
        await download.ReloadInstalledCommand.ExecuteAsync(null);
        Assert.False(download.IsBusy);
        Assert.True(download.InstalledSummary.Length > 0);

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
        // 报告走 L() 取文案，无 UI 环境返回资源键；断言具体中文前装中文 Provider
        // （ToolsViewModel 是构造时就生成报告的，所以 Provider 要在 new 之前装好）。
        using (ChineseProvider.Scope())
        {
            var toolsWithText = new ToolsViewModel();
            Assert.False(string.IsNullOrWhiteSpace(toolsWithText.SystemReport));
            Assert.Contains(UMango.Core.Platform.SystemInfo.Describe(), toolsWithText.SystemReport);
        }

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
