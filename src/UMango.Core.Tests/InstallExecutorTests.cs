using UMango.Core.App.Tasks;
using UMango.Core.Execution;
using UMango.Core.Install;
using UMango.Core.PackageManagers;
using UMango.Core.PackageManagers.Adapters;
using UMango.Core.Records;
using UMango.Core.Terminal;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 安装执行器的链路测试。
///
/// **不真的安装任何东西**：进程执行通过构造函数注入一个假的运行器，
/// 由它按脚本吐出行，于是「子项进度来源 + 终端回传 + 记录落盘」这些链路
/// 都可以在无副作用的前提下被验证。
/// </summary>
public class InstallExecutorTests
{
    private static (InstallExecutor Executor, List<string> Emitted, InstallRecordStore Store) Build(
        string[] scriptedLines, int exitCode = 0, bool timedOut = false)
    {
        var registry = new PackageManagerRegistry([new NpmPackageManager()]);
        var store = new InstallRecordStore(Path.Combine(Path.GetTempPath(), $"umango-rec-{Guid.NewGuid():N}.json"));
        var emitted = new List<string>();

        var executor = new InstallExecutor(registry, store, (spec, onLine, ct) =>
        {
            emitted.Add(spec.CommandLine);
            foreach (var line in scriptedLines)
                onLine?.Invoke(false, line);
            var stderr = exitCode == 0 ? "" : "boom: something failed";
            if (exitCode != 0) onLine?.Invoke(true, stderr);
            return Task.FromResult(new ProcessOutput
            {
                ExitCode = exitCode, StdOut = string.Join('\n', scriptedLines),
                StdErr = stderr, TimedOut = timedOut, DurationMs = 12
            });
        });
        return (executor, emitted, store);
    }

    [Fact]
    public async Task 按下载方式的子项被建立出来且状态推进到完成()
    {
        var (executor, _, _) = Build([
            "resolving dependencies...",
            "fetching metadata",
            "downloading 40%",
            "downloading 100%",
            "added 3 packages"
        ]);

        var (task, result) = await executor.InstallAsync(
            new InstallRequest(PackageSourceKind.Npm, "is-number", "7.0.0"));

        Assert.True(result.Success);
        // 子项来自 npm 适配器的 PlanInstallAsync（按下载方式划分）
        Assert.True(task.Children.Count > 1, "应当有多个按下载方式划分的子项");
        Assert.All(task.Children, c => Assert.Equal(TaskState.Success, c.State));
        Assert.All(task.Children, c => Assert.Equal(1.0, c.Progress, 3));
        Assert.Equal(TaskState.Success, task.State);
    }

    [Fact]
    public async Task 子项进度按输出百分比更新且父进度加权汇总()
    {
        var (executor, _, _) = Build([
            "resolving dependencies",
            "downloading 50%",
        ]);

        var (task, _) = await executor.InstallAsync(new InstallRequest(PackageSourceKind.Npm, "x"));

        // 父进度必须是加权结果，落在 0~1 之间且 > 0
        Assert.InRange(task.Progress, 0.0, 1.0);
        Assert.True(task.Progress > 0, "至少应当有子项推进带来了进度");
    }

    [Fact]
    public async Task 终端输出被原生回传并挂在对应子项下()
    {
        var (executor, _, _) = Build(["downloading 30%", "added 1 package"]);
        var (task, _) = await executor.InstallAsync(new InstallRequest(PackageSourceKind.Npm, "y"));

        var bridge = TerminalBridge.For(task.Title);
        Assert.True(bridge.Count >= 3, $"终端桥应当收到命令行与输出，实际 {bridge.Count} 行");
        // 输出被归属到子项（不是一坨无归属的黑底文本）
        var attributed = bridge.Lines.Count(l => l.Step is not null);
        Assert.True(attributed > 0, "至少有一部分输出应当挂到具体子项下");
    }

    [Fact]
    public async Task 失败时任务标记为失败并写入错误码()
    {
        var (executor, _, store) = Build(["resolving deps"], exitCode: 1);

        var (task, result) = await executor.InstallAsync(new InstallRequest(PackageSourceKind.Npm, "z"));

        Assert.False(result.Success);
        Assert.Equal("command_failed", result.ErrorCode);
        Assert.Equal(TaskState.Failed, task.State);
        var rec = Assert.Single(store.Records);
        Assert.False(rec.Success);
        Assert.Equal("command_failed", rec.ErrorCode);
    }

    [Fact]
    public async Task 记录里带上包名与安装目录()
    {
        var (executor, _, store) = Build(["added 1 package"]);
        await executor.InstallAsync(new InstallRequest(PackageSourceKind.Npm, "left-pad", "1.3.0"));

        var rec = Assert.Single(store.Records);
        Assert.Equal(PackageSourceKind.Npm, rec.Source);
        Assert.Equal("left-pad", rec.PackageName);
        Assert.Equal("1.3.0", rec.Version);
        Assert.Equal("install", rec.Action);
        // 子标题要显示的安装目录必须被记录下来（探测过则用真实路径，否则平台默认）
        Assert.False(string.IsNullOrWhiteSpace(rec.InstallDirectory));
    }

    [Fact]
    public async Task dryRun只规划不执行()
    {
        var (executor, emitted, store) = Build([]);
        var (task, result) = await executor.InstallAsync(
            new InstallRequest(PackageSourceKind.Npm, "dry"), dryRun: true);

        Assert.True(result.Success);
        Assert.Empty(emitted);                       // 没有真的跑命令
        Assert.Empty(store.Records);                 // 不写记录
        Assert.True(task.Children.Count > 1);
        Assert.All(task.Children, c => Assert.Equal("已规划", c.StateMessage));
    }

    [Fact]
    public async Task 平台不支持的包源直接失败且不执行命令()
    {
        var registry = new PackageManagerRegistry([
            new WingetPackageManager(), new NpmPackageManager()
        ]);
        var store = new InstallRecordStore(Path.Combine(Path.GetTempPath(), $"umango-rec-{Guid.NewGuid():N}.json"));
        var called = false;
        var executor = new InstallExecutor(registry, store, (_, _, _) =>
        {
            called = true;
            return Task.FromResult(new ProcessOutput { ExitCode = 0 });
        });

        var (task, result) = await executor.InstallAsync(new InstallRequest(PackageSourceKind.Winget, "Git.Git"));

        // 若当前平台是 Windows 则该用例不适用（winget 受支持）
        if (UMango.Core.Platform.SystemInfo.IsWindows) return;

        Assert.False(result.Success);
        Assert.Equal("unsupported_platform", result.ErrorCode);
        Assert.False(called, "平台不支持时不得执行任何命令");
        Assert.Equal(TaskState.Failed, task.State);
    }

    [Fact]
    public void 下载方式到阶段名的映射覆盖全部枚举值()
    {
        foreach (DownloadMethod m in Enum.GetValues<DownloadMethod>())
        {
            var name = InstallExecutor.MethodToStepName(m);
            Assert.Contains(name, StepNames.Ordered);
        }
    }

    [Fact]
    public void 命令拼装按包源与作用域区分()
    {
        var (p1, a1, _) = InstallCommandBuilder.BuildInstall(PackageSourceKind.Npm, "x", "1.0.0", global: true);
        Assert.Equal("npm", p1);
        Assert.Contains("-g", a1);
        Assert.Contains("x@1.0.0", a1);

        var (_, a2, _) = InstallCommandBuilder.BuildInstall(PackageSourceKind.Npm, "x", null, global: false);
        Assert.DoesNotContain("-g", a2);

        var (p3, a3, _) = InstallCommandBuilder.BuildInstall(PackageSourceKind.Homebrew, "wget", null, false);
        Assert.Equal("brew", p3);
        Assert.Contains("install", a3);

        var (_, a4, _) = InstallCommandBuilder.BuildInstall(PackageSourceKind.Git, "owner/repo", null, false);
        Assert.Contains("clone", a4);
    }

    [Fact]
    public void git简写名被补全为可克隆URL()
    {
        Assert.Equal("https://github.com/owner/repo", InstallCommandBuilder.NormalizeGitUrl("owner/repo"));
        Assert.Equal("https://gitlab.com/x/y", InstallCommandBuilder.NormalizeGitUrl("https://gitlab.com/x/y"));
    }
}
