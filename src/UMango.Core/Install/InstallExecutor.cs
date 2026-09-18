using UMango.Core.App.Tasks;
using UMango.Core.Execution;
using UMango.Core.PackageManagers;
using UMango.Core.Logging;
using UMango.Core.Platform;
using UMango.Core.Records;
using UMango.Core.Terminal;

namespace UMango.Core.Install;

/// <summary>一次安装请求。</summary>
public sealed record InstallRequest(
    PackageSourceKind Source,
    string Name,
    string? Version = null,
    bool Global = true,
    string? WorkingDirectory = null);

/// <summary>安装结果。</summary>
public sealed record InstallResult(bool Success, string? ErrorCode, string? Message, long DurationMs);

/// <summary>
/// 安装执行器。
///
/// 把四件事串成一条链（这正是「各包管理器输出回传图形化用户界面」的落地处）：
///   1. 用适配器的 <see cref="IPackageManager.PlanInstallAsync"/> 得到**按下载方式划分的子项**，
///      据此建立 TaskModel 的 Children（每个子项一条进度）；
///   2. 用 <see cref="ProcessRunner"/> 真正执行安装命令，**逐行**流式回传；
///   3. 每一行经 <see cref="InstallProgressParser"/> 归类到某个子项并解析百分比，
///      归不进任何子项的落到"其他"；父进度按子项权重加权汇总；
///   4. 结束后写入 <see cref="InstallRecordStore"/>（管理记录与次数记录）。
///
/// **可测试性**：进程执行通过构造函数注入，
/// 因此单元测试可以喂入假的 CLI 输出流，无需真的安装任何东西。
///
/// **⚠️ 线程约定（不要"优化"掉）**：本类会修改 <see cref="TaskCenter.Tasks"/> 与
/// <see cref="TaskModel.Children"/> 这类**绑定到界面的 ObservableCollection**，
/// 而 WPF 不允许从非 UI 线程改它们。因此下面所有 await **故意不加
/// ConfigureAwait(false)** —— 那样会把后续代码切到线程池线程上执行，
/// 运行时会抛「This type of CollectionView does not support changes to its
/// SourceCollection from a different thread」。
/// 保留调用方的同步上下文，就等于自动回到 UI 线程。
/// 已有一条回归测试专门锁住这一点（见 InstallExecutorTests 的线程亲和性用例）。
/// </summary>
public sealed class InstallExecutor
{
    private readonly PackageManagerRegistry _registry;
    private readonly InstallRecordStore _records;
    private readonly Func<ProcessSpec, ProcessRunner.OutputLineHandler?, CancellationToken, Task<ProcessOutput>> _run;

    public InstallExecutor(
        PackageManagerRegistry registry,
        InstallRecordStore records,
        Func<ProcessSpec, ProcessRunner.OutputLineHandler?, CancellationToken, Task<ProcessOutput>>? runner = null)
    {
        _registry = registry;
        _records = records;
        _run = runner ?? ((spec, onLine, ct) => ProcessRunner.RunAsync(spec, onLine, ct));
    }

    /// <summary>只做规划不执行（界面在确认弹窗里展示步骤用）。</summary>
    public async Task<IReadOnlyList<InstallStep>> PlanAsync(
        PackageSourceKind kind, string name, string? version, CancellationToken ct = default)
    {
        var pm = _registry.Find(kind);
        if (pm is null) return [];
        return await pm.PlanInstallAsync(name, version, ct).ConfigureAwait(false);
    }

    /// <summary>执行安装，返回一个已注册到 TaskCenter 的任务（其 Children 即子项进度）。</summary>
    public async Task<(TaskModel Task, InstallResult Result)> InstallAsync(
        InstallRequest request, bool dryRun = false, CancellationToken ct = default)
    {
        var pm = _registry.Find(request.Source);
        var sourceName = request.Source.DisplayName();
        var title = $"安装 {request.Name}（{sourceName}）";

        var task = TaskModel.Group(title);
        task.State = TaskState.Running;
        task.StateMessage = "准备中";
        TaskCenter.Register(task);

        var bridge = TerminalBridge.For(task.Title);
        bridge.BeginStep(null);
        bridge.Write(false, $"$ {InstallCommandBuilder.BuildInstall(request.Source, request.Name, request.Version, request.Global).Explanation}");

        var startedAt = DateTimeOffset.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Logging.Log.Info("Install", $"开始：{title}，版本 {request.Version ?? "(最新)"}，全局 {request.Global}");

        // 1) 建立子项（按下载方式划分）
        // 注意：这里刻意不写 ConfigureAwait(false) —— 见类注释的线程约定
        var steps = await PlanAsync(request.Source, request.Name, request.Version, ct);
        var children = new List<(InstallStep Step, TaskModel Model)>();
        foreach (var step in steps)
        {
            var child = TaskModel.Progressive(step.Title);
            child.State = TaskState.Waiting;
            child.StateMessage = "等待中";
            task.Children.Add(child);
            children.Add((step, child));
        }

        if (request.Source.Supports(SystemInfo.Platform) is false)
        {
            const string detail = "当前平台不支持该包源";
            task.State = TaskState.Failed;
            task.StateMessage = detail;
            bridge.Write(true, detail);
            Record(request, false, "unsupported_platform", detail, startedAt, sw.ElapsedMilliseconds);
            return (task, new InstallResult(false, "unsupported_platform", detail, sw.ElapsedMilliseconds));
        }

        if (dryRun)
        {
            foreach (var (_, m) in children) { m.State = TaskState.Success; m.Progress = 1; m.StateMessage = "已规划"; }
            task.State = TaskState.Success;
            task.StateMessage = "仅规划（未执行）";
            return (task, new InstallResult(true, null, "dry-run", sw.ElapsedMilliseconds));
        }

        // 2) 执行 + 流式回传 + 归类到子项
        var (program, args, _) = InstallCommandBuilder.BuildInstall(
            request.Source, request.Name, request.Version, request.Global);

        var current = (TaskModel?)null;
        void OnLine(bool isStdErr, string line)
        {
            var stepName = InstallProgressParser.ClassifyLine(line);
            if (stepName is not null)
            {
                // 切换阶段：上一个阶段视为完成
                var matched = children.FirstOrDefault(c =>
                    MethodToStepName(c.Step.Method) == stepName).Model;
                if (matched is not null && !ReferenceEquals(matched, current))
                {
                    if (current is not null && current.State == TaskState.Running)
                    {
                        current.Progress = 1;
                        current.State = TaskState.Success;
                        current.StateMessage = "完成";
                    }
                    matched.State = TaskState.Running;
                    matched.StateMessage = "进行中";
                    current = matched;
                    bridge.BeginStep(matched.Title);
                }
            }

            // 进度百分比：优先归到当前阶段
            var pct = InstallProgressParser.ParsePercent(line);
            if (pct is not null && current is not null)
            {
                current.Progress = Math.Max(current.Progress, pct.Value);
                current.StateMessage = $"{pct.Value * 100:0}%";
            }

            bridge.Write(isStdErr, line);
            AggregateWeighted(task, children);
        }

        var spec = new ProcessSpec
        {
            Program = program,
            Args = args,
            WorkingDirectory = request.WorkingDirectory,
            TimeoutMs = 30 * 60 * 1000,   // 安装可能很久（源码编译），给 30 分钟
        };

        ProcessOutput output;
        try
        {
            // 同样不写 ConfigureAwait(false)：OnLine 回调会更新界面绑定的属性与集合
            output = await _run(spec, OnLine, ct);
        }
        catch (ProcessException ex)
        {
            Logging.Log.Error("Install", ex, $"{title} 抛出 {ex.Code}");
            task.State = TaskState.Failed;
            task.StateMessage = ex.Message;
            bridge.Write(true, ex.Message);
            Record(request, false, ex.Code, ex.Message, startedAt, sw.ElapsedMilliseconds);
            return (task, new InstallResult(false, ex.Code, ex.Message, sw.ElapsedMilliseconds));
        }

        // 3) 收尾：所有子项按最终结果落定
        foreach (var (_, m) in children)
        {
            if (m.State is TaskState.Waiting or TaskState.Running)
            {
                m.State = output.Success ? TaskState.Success : TaskState.Failed;
                if (output.Success) { m.Progress = 1; m.StateMessage = "完成"; }
                else m.StateMessage = "未完成";
            }
        }
        AggregateWeighted(task, children);

        var ok = output.Success;
        if (ok)
        {
            Logging.Log.Info("Install", $"{title} 完成（{output.DurationMs} ms）");
        }
        else
        {
            Logging.Log.Warn("Install",
                $"{title} 失败（退出码 {output.ExitCode}，{output.DurationMs} ms）：{Truncate(output.StdErr, 300)}");
        }

        task.State = ok ? TaskState.Success : TaskState.Failed;
        task.StateMessage = ok ? "安装完成" : $"失败（退出码 {output.ExitCode}）";
        bridge.Write(!ok, $"—— 退出码 {output.ExitCode}，耗时 {output.DurationMs} ms ——");
        TaskCenter.Complete(task, ok);

        var errCode = ok ? null : (output.TimedOut ? "timeout" : "command_failed");
        Record(request, ok, errCode, ok ? null : Truncate(output.StdErr, 500), startedAt, output.DurationMs);
        sw.Stop();
        return (task, new InstallResult(ok, errCode, ok ? null : Truncate(output.StdErr, 200), sw.ElapsedMilliseconds));
    }

    /// <summary>按子项权重加权汇总父进度（权重来自适配器的 InstallStep.Weight）。</summary>
    private static void AggregateWeighted(TaskModel parent, List<(InstallStep Step, TaskModel Model)> children)
    {
        if (children.Count == 0) return;
        double totalWeight = children.Sum(c => c.Step.Weight);
        if (totalWeight <= 0) totalWeight = children.Count;
        var sum = children.Sum(c => c.Model.Progress * (c.Step.Weight > 0 ? c.Step.Weight : 1));
        parent.Progress = Math.Clamp(sum / totalWeight, 0, 1);
    }

    /// <summary>把 InstallStep.Method 映射到统一的阶段名，用于把 CLI 输出归类到子项。</summary>
    public static string MethodToStepName(DownloadMethod method) => method switch
    {
        DownloadMethod.DependencyResolve => StepNames.ResolveDependencies,
        DownloadMethod.RegistryMetadata or DownloadMethod.ManifestFetch => StepNames.FetchManifest,
        DownloadMethod.RegistryTarball or DownloadMethod.DirectUrl or DownloadMethod.BottleDownload
            or DownloadMethod.GitClone or DownloadMethod.Aria2 or DownloadMethod.BitTorrent
            or DownloadMethod.Ed2k => StepNames.Download,
        DownloadMethod.SourceBuild => StepNames.Build,
        DownloadMethod.LinkWrite or DownloadMethod.GitCheckout => StepNames.LinkWrite,
        _ => StepNames.LinkWrite
    };

    private void Record(InstallRequest req, bool success, string? code, string? detail,
                        DateTimeOffset at, long ms)
    {
        _records.Add(new InstallRecord
        {
            Source = req.Source,
            PackageName = req.Name,
            Version = req.Version,
            Action = "install",
            OccurredAt = at,
            Success = success,
            InstallDirectory = _registry.Find(req.Source)?.InstallDirectory,
            DurationMs = ms,
            ErrorCode = code,
            Detail = detail
        });
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
