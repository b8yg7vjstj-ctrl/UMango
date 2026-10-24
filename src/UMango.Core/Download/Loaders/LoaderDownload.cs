// SPDX-License-Identifier: Apache-2.0
//
// 下载加载器：把「一个加载器 = 一次批量下载」这件事接进加载器框架。
//
// **设计来源**：PCL-CE 的
// `Plain Craft Launcher 2/Modules/Network/Loaders/LoaderDownload.cs`（229 行）
// 与 `LoaderDownloadUnc.cs`（65 行）。
// 这两个文件位于 PCL 的**自定义许可目录**（《PCL 分发有限许可》），
// 因此 UMango **只借鉴设计语义，独立重写，不复制任何代码**。
//
// 与 PCL 原版的对应关系：
//   PCL `LoaderDownload`            →  本 `LoaderDownload`
//   PCL `LoaderDownloadUnc`         →  本 `LoaderUncDownload`
//   PCL `PCL.Network.DownloadFile`  →  本 `DownloadFile`（早已存在于 Core）
//   PCL `PCL.Network.NetState`      →  本 `DownloadFileState`
//
// 与 PCL 原版的差异（都是改进，不是抄）：
//   ① PCL 用**静态全局** `ModNet.NetTaskConnectionLimit` 控制并行文件数；
//      UMango 走 `DownloadResources.Current`（可被配置观察者动态改）。
//   ② PCL 的 `Progress` 是**各文件进度的算术平均**（不分文件大小）；
//      UMango 保留这个口径（与 PCL 一致，且界面读起来直观），
//      但同时提供**按字节加权**的 `WeightedProgress` 供需要时使用。
//   ③ PCL 的下载文件"已完成跳过"靠 `DownloadFile.Check`（MC 专属的 mod 校验）；
//      UMango 用 Core 已有的 `DownloadFile.IsComplete()`（存在 + 非空 / SHA-256 匹配）。
//   ④ PCL 一旦某个文件失败就**取消整批**（`_cancellationTokenSource?.Cancel()`）；
//      UMango 默认**继续下载其余文件**，最后汇总失败 —— 但保留
//      `FailFast` 开关可恢复 PCL 那种行为。
//   ⑤ PCL 的 `Abort` 不区分"没开始"和"已完成"；UMango 遵循本仓库其它加载器的
//      约定：等待态也能中止，已完成态不动。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Loaders;
using UMango.Core.Download.Resources;
using UMango.Core.Terminal;

namespace UMango.Core.Download.Loaders;

/// <summary>单个下载文件的状态。</summary>
public enum DownloadFileState
{
    /// <summary>还没开始。</summary>
    Waiting,

    /// <summary>正在连接/下载。</summary>
    Downloading,

    /// <summary>已完成。</summary>
    Finished,

    /// <summary>失败或被中止。</summary>
    Interrupted,
}

/// <summary>加载器视角下的一个下载文件（含运行时状态）。</summary>
public sealed class DownloadFileEntry
{
    /// <summary>创建条目。</summary>
    public DownloadFileEntry(DownloadFile file, IReadOnlyList<DownloadSource>? sources = null)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Sources = sources is { Count: > 0 }
            ? sources
            : [DownloadSource.Primary(file.Url)];
    }

    /// <summary>文件定义。</summary>
    public DownloadFile File { get; }

    /// <summary>候选来源（第一个是主源）。</summary>
    public IReadOnlyList<DownloadSource> Sources { get; }

    /// <summary>当前状态。</summary>
    public DownloadFileState State { get; internal set; } = DownloadFileState.Waiting;

    /// <summary>已下载字节数（拿不到就是 null，不编造）。</summary>
    public long? DownloadedBytes { get; internal set; }

    /// <summary>总字节数（拿不到就是 null）。</summary>
    public long? TotalBytes { get; internal set; }

    /// <summary>错误信息。</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>是否是"目标已存在且内容正确、直接跳过"的那一种完成。</summary>
    public bool Skipped { get; internal set; }

    /// <summary>该文件的进度（0..1）。不知道总大小则返回 null。</summary>
    public double? Progress =>
        TotalBytes is { } total && total > 0
            ? Math.Clamp((DownloadedBytes ?? 0d) / total, 0d, 1d)
            : null;

    /// <summary>本地路径。</summary>
    public string LocalPath => File.TargetPath;
}

/// <summary>
/// 批量下载加载器。
///
/// 一个加载器代表**一批文件**，逐个（或并行几个）下载；整体进度是各文件进度的平均。
/// </summary>
public sealed class LoaderDownload : LoaderBase
{
    private readonly DownloadTaskFacade _facade;
    private readonly TerminalBridge? _bridge;
    private readonly Action<DownloadFileEntry>? _onFileChanged;
    private readonly object _fileLock = new();

    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<DownloadFileEntry> _entries = [];
    private int _finishedCount;
    private int _failFastTriggered;

    /// <summary>创建加载器。</summary>
    /// <param name="name">显示名。</param>
    /// <param name="files">要下载的文件。</param>
    /// <param name="facade">下载门面（负责重试 + 换源）。</param>
    /// <param name="bridge">终端桥（可选）。</param>
    /// <param name="onFileChanged">单个文件状态变化回调（可选）。</param>
    /// <param name="failFast">某个文件失败后是否放弃整批（PCL 原版行为）。默认 false。</param>
    public LoaderDownload(
        string name,
        IEnumerable<DownloadFileEntry> files,
        DownloadTaskFacade facade,
        TerminalBridge? bridge = null,
        Action<DownloadFileEntry>? onFileChanged = null,
        bool failFast = false)
    {
        Name = name;
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _bridge = bridge;
        _onFileChanged = onFileChanged;
        FailFast = failFast;
        _entries = (files ?? []).ToList();
    }

    /// <summary>某个文件失败后是否放弃整批。</summary>
    public bool FailFast { get; set; }

    /// <summary>本次下载涉及的文件。</summary>
    public IReadOnlyList<DownloadFileEntry> Files
    {
        get { lock (_fileLock) return _entries; }
    }

    private int _failCount;

    /// <summary>失败的文件数。</summary>
    public int FailCount => Volatile.Read(ref _failCount);

    /// <summary>
    /// 整体进度：各文件进度的算术平均（与 PCL 一致）。
    ///
    /// 没有任何文件报告进度时返回 0（不确定态由 <see cref="LoaderBase.HasProgress"/> 表达）。
    /// </summary>
    public double AggregatedProgress
    {
        get
        {
            var files = Files;
            if (files.Count == 0) return 1d;

            var known = files
                .Select(f => f.Progress)
                .Where(p => p is not null)
                .Select(p => p!.Value)
                .ToList();

            if (known.Count == 0) return 0d;
            return Math.Clamp(known.Average(), 0d, 1d);
        }
    }

    /// <summary>
    /// 按字节加权的进度（大文件主导）。
    ///
    /// 与 <see cref="Progress"/> 的算术平均不同：算术平均会让"1 KB 的小文件"
    /// 和"1 GB 的大文件"在界面上占同样分量。
    /// </summary>
    public double WeightedProgress
    {
        get
        {
            var files = Files.Where(f => f.TotalBytes is > 0).ToList();
            if (files.Count == 0) return AggregatedProgress;

            long total = 0;
            long done = 0;
            foreach (var f in files)
            {
                total += f.TotalBytes!.Value;
                done += f.DownloadedBytes ?? 0;
            }
            return total <= 0 ? AggregatedProgress : Math.Clamp((double)done / total, 0d, 1d);
        }
    }

    /// <summary>启动下载。</summary>
    public override void Start(object? input = null, bool isForceRestart = false)
    {
        if (input is IEnumerable<DownloadFileEntry> typed)
        {
            lock (_fileLock)
            {
                _entries = typed.ToList();
                _finishedCount = 0;
            }
            Interlocked.Exchange(ref _failCount, 0);
        }

        if (State == LoaderState.Running)
            return;

        foreach (var f in Files)
        {
            f.State = DownloadFileState.Waiting;
            f.ErrorMessage = null;
            f.Skipped = false;
            f.DownloadedBytes = null;
            f.TotalBytes = null;
        }

        Interlocked.Exchange(ref _failCount, 0);
        Interlocked.Exchange(ref _failFastTriggered, 0);
        ResetProgress();
        State = LoaderState.Running;

        var cts = new CancellationTokenSource();
        _cancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                State = LoaderState.Aborted;
            }
            catch (Exception ex)
            {
                Error = ex;
                State = LoaderState.Failed;
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var files = Files;
        if (files.Count == 0)
        {
            State = LoaderState.Succeeded;
            return;
        }

        // FailFast 模式：**按顺序一个个来**。
        // 这样"第一个失败就放弃整批"才有确定含义 —— 并行跑的话，
        // 几个文件可能同时开跑，放弃就变成了"放弃还没开的那些"，语义模糊。
        if (FailFast)
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                try
                {
                    await ProcessFileAsync(file, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 中止：不算失败
                }
                catch (Exception ex)
                {
                    file.State = DownloadFileState.Interrupted;
                    file.ErrorMessage = ex.Message;
                    Interlocked.Increment(ref _failCount);
                    Notify(file);
                    Interlocked.Exchange(ref _failFastTriggered, 1);
                    MarkRemainingAsInterrupted();
                    Error = ex;
                    State = LoaderState.Failed;
                    return;
                }

                // 前面某个文件失败了 → 剩下的不再跑
                if (Volatile.Read(ref _failFastTriggered) == 1)
                {
                    MarkRemainingAsInterrupted();
                    var failCountNow = Files.Count(f => f.State == DownloadFileState.Interrupted);
                    var firstErrorNow = Files.FirstOrDefault(f => f.State == DownloadFileState.Interrupted)?.ErrorMessage;
                    Error = new InvalidOperationException(
                        $"{failCountNow} 个文件下载失败" + (firstErrorNow is null ? "。" : $"：{firstErrorNow}"));
                    State = LoaderState.Failed;
                    return;
                }
            }

            Progress = 1d;
            State = LoaderState.Succeeded;
            return;
        }

        // 并行文件数上限：不超过全局连接额度，也不超过文件数。
        var maxParallel = Math.Max(1, Math.Min(files.Count, DownloadResources.Current.MaxConnections));
        using var semaphore = new SemaphoreSlim(maxParallel);

        var tasks = files.Select(async file =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ProcessFileAsync(file, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 中止：不算失败。
            }
            catch (Exception ex)
            {
                file.State = DownloadFileState.Interrupted;
                file.ErrorMessage = ex.Message;
                Interlocked.Increment(ref _failCount);
                Notify(file);
                if (FailFast)
                {
                    // 置位后再抛：让**还没开始**的文件在真正开跑前就看到"整批已放弃"，
                    // 否则 FailFast 只是"最后把剩余文件标成失败"，它们其实照样下了。
                    Interlocked.Exchange(ref _failFastTriggered, 1);
                    throw;
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkRemainingAsInterrupted();
            State = LoaderState.Aborted;
            return;
        }
        catch (Exception ex)
        {
            // FailFast 模式：某个文件失败导致整批中止。
            MarkRemainingAsInterrupted();
            Error = ex;
            State = LoaderState.Failed;
            return;
        }

        // 全部跑完：按结果定终态。
        var failed = Files.Count(f => f.State == DownloadFileState.Interrupted);
        Progress = 1d;
        if (failed == 0)
        {
            State = LoaderState.Succeeded;
            return;
        }

        // 失败时必须记录 Error —— LoaderBase.WaitForExitAsync 依赖它抛出有意义的异常。
        var firstError = Files.FirstOrDefault(f => f.State == DownloadFileState.Interrupted)?.ErrorMessage;
        Error = new InvalidOperationException(
            $"{failed} 个文件下载失败" + (firstError is null ? "。" : $"：{firstError}"));
        State = LoaderState.Failed;
    }

    private async Task ProcessFileAsync(DownloadFileEntry file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // FailFast 已触发：这次下载就此放弃，不再真的发出请求。
        if (Volatile.Read(ref _failFastTriggered) == 1)
        {
            file.State = DownloadFileState.Interrupted;
            file.ErrorMessage = "整批已放弃（前一个文件失败）";
            Notify(file);
            return;
        }

        // 目标已存在且内容正确 → 跳过（PCL 的 DownloadFile.Check 语义）。
        if (file.File.IsComplete())
        {
            file.Skipped = true;
            file.State = DownloadFileState.Finished;
            TryReadLocalSize(file);
            Notify(file);
            return;
        }

        var directory = Path.GetDirectoryName(file.LocalPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        file.State = DownloadFileState.Downloading;
        Notify(file);

        var request = new DownloadRequest(
            file.Sources[0].Url,
            directory ?? ".",
            Path.GetFileName(file.LocalPath),
            file.File.ExpectedSha256,
            MaxBytesPerSecond: DownloadResources.Current.SpeedLimitBytesPerSecond,
            SkipExisting: false);

        var result = await _facade.DownloadAsync(
            file.Sources,
            request,
            _bridge,
            progress => OnProgress(file, progress),
            attempt => OnAttempt(file, attempt),
            policy: null,
            ct: ct).ConfigureAwait(false);

        if (result.Success)
        {
            file.State = DownloadFileState.Finished;
            TryReadLocalSize(file);
            Notify(file);
        }
        else
        {
            file.State = DownloadFileState.Interrupted;
            file.ErrorMessage = result.ErrorMessage ?? result.ErrorCode ?? "下载失败";
            Interlocked.Increment(ref _failCount);
            Notify(file);
            if (FailFast) Interlocked.Exchange(ref _failFastTriggered, 1);
        }
    }

    /// <summary>
    /// 读本地文件大小填进条目。
    ///
    /// 拿不到就**保持 null**（不编造数字）—— 界面据此显示"大小未知"而不是显示 0。
    /// </summary>
    private static void TryReadLocalSize(DownloadFileEntry file)
    {
        try
        {
            if (!File.Exists(file.LocalPath)) return;
            var length = new FileInfo(file.LocalPath).Length;
            file.TotalBytes = length;
            file.DownloadedBytes = length;
        }
        catch (Exception)
        {
            // 读不到就当作未知
        }
    }

    private void OnProgress(DownloadFileEntry file, DownloadProgress progress)
    {
        file.DownloadedBytes = progress.BytesDone ?? file.DownloadedBytes;
        file.TotalBytes = progress.BytesTotal ?? file.TotalBytes;
        Notify(file);
    }

    private void OnAttempt(DownloadFileEntry file, DownloadAttempt attempt)
    {
        if (attempt.Success) return;
        file.ErrorMessage = attempt.FailureSummary ?? file.ErrorMessage;
        Notify(file);
    }

    private void MarkRemainingAsInterrupted()
    {
        foreach (var f in Files.Where(f => f.State is DownloadFileState.Waiting or DownloadFileState.Downloading))
        {
            f.State = DownloadFileState.Interrupted;
            Notify(f);
        }
    }

    private void Notify(DownloadFileEntry file)
    {
        _onFileChanged?.Invoke(file);

        // 汇总进度：每有文件完成就更新一次（其余时刻靠进度回调驱动）。
        var files = Files;
        var done = files.Count(f => f.State == DownloadFileState.Finished);
        if (done != _finishedCount)
        {
            _finishedCount = done;
            Progress = AggregatedProgress;
        }
    }

    /// <summary>中止下载。</summary>
    public override void Abort()
    {
        if (State == LoaderState.Waiting)
        {
            State = LoaderState.Aborted;
            return;
        }

        if (State != LoaderState.Running)
            return;

        var cts = _cancellation;
        _cancellation = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已结束，忽略
        }

        State = LoaderState.Aborted;
    }
}

/// <summary>
/// UNC / 本地文件复制加载器。
///
/// 对应 PCL 的 `LoaderDownloadUnc`（从局域网共享复制文件到本地）。
/// </summary>
public sealed class LoaderUncDownload : LoaderBase
{
    private readonly object _pathLock = new();
    private string _sourcePath;
    private string _targetPath;
    private CancellationTokenSource? _cancellation;

    /// <summary>创建加载器。</summary>
    public LoaderUncDownload(string name, string sourcePath, string targetPath)
    {
        Name = name;
        _sourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        _targetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
    }

    /// <summary>源路径（UNC 或本地路径）。</summary>
    public string SourcePath
    {
        get { lock (_pathLock) return _sourcePath; }
        private set { lock (_pathLock) _sourcePath = value; }
    }

    /// <summary>目标路径。</summary>
    public string TargetPath
    {
        get { lock (_pathLock) return _targetPath; }
        private set { lock (_pathLock) _targetPath = value; }
    }

    /// <summary>启动复制。</summary>
    public override void Start(object? input = null, bool isForceRestart = false)
    {
        if (input is Tuple<string, string> tuple)
        {
            SourcePath = tuple.Item1;
            TargetPath = tuple.Item2;
        }
        else if (input is ValueTuple<string, string> vt)
        {
            SourcePath = vt.Item1;
            TargetPath = vt.Item2;
        }

        if (State == LoaderState.Running)
            return;

        ResetProgress();
        State = LoaderState.Running;

        var cts = new CancellationTokenSource();
        _cancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // 目标目录由 Files.CopyFileAsync 自己创建（它内部有 Directory.CreateDirectory），
                // 这里不重复创建 —— 少一处重复逻辑，也少一处"两边的创建规则不一致"的隐患。
                await UMango.Core.IO.Files.CopyFileAsync(SourcePath, TargetPath, cts.Token).ConfigureAwait(false);

                Progress = 1d;
                State = LoaderState.Succeeded;
            }
            catch (OperationCanceledException)
            {
                State = LoaderState.Aborted;
            }
            catch (Exception ex)
            {
                Error = ex;
                State = LoaderState.Failed;
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    /// <summary>中止复制。</summary>
    public override void Abort()
    {
        if (State == LoaderState.Waiting)
        {
            State = LoaderState.Aborted;
            return;
        }

        if (State != LoaderState.Running)
            return;

        var cts = _cancellation;
        _cancellation = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已结束，忽略
        }

        State = LoaderState.Aborted;
    }
}
