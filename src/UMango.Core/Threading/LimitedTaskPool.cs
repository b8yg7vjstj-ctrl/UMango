// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Threading/LimitedTaskPool.cs 与 DualThreadPool.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Threading → UMango.Core.Threading。
//   2. 原版依赖 `LimitedConcurrencyLevelTaskScheduler`（一个自定义 TaskScheduler）。
//      UMango 已有 `LimitedConcurrencyTaskScheduler`（信号量实现，行为等价），
//      这里直接用它，不再引入第二套并发限制机制。
//   3. 增加 `Dispose()`：原版的 CancellationTokenSource 从不释放。
//   4. DualThreadPool 的 CPU/IO 两个池在原版里**各占 maxThread 个线程**，
//      总共是 2×maxThread。UMango 保留这个语义但把默认值改成 2，
//      并在文档里写清楚"总数 = 2 × maxThread"。

using System.Threading;

namespace UMango.Core.Threading;

/// <summary>
/// 限制并发数的任务池。
///
/// 与直接 <c>Task.Run</c> 的区别：这里保证同时运行的委托不超过
/// <see cref="MaxThread"/> 个。UMango 的典型用法是"探测六个包管理器的 CLI" ——
/// 同时起六个进程在低配机器上会卡顿。
/// </summary>
public sealed class LimitedTaskPool : IDisposable
{
    /// <summary>最大并发数。</summary>
    public int MaxThread { get; }

    private readonly LimitedConcurrencyTaskScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public LimitedTaskPool(int maxThread)
    {
        if (maxThread < 1) throw new ArgumentOutOfRangeException(nameof(maxThread));
        MaxThread = maxThread;
        _scheduler = new LimitedConcurrencyTaskScheduler(maxThread);
    }

    /// <summary>提交一个同步任务。</summary>
    public Task Submit(Action work) => Submit(work, _cts.Token);

    /// <summary>提交一个同步任务，可指定额外的取消信号。</summary>
    public Task Submit(Action work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _scheduler.QueueAsync(() => { work(); return Task.CompletedTask; }, cancellationToken);
    }

    /// <summary>提交一个异步任务。</summary>
    public Task Submit(Func<Task> work) => Submit(work, _cts.Token);

    /// <summary>提交一个异步任务，可指定额外的取消信号。</summary>
    public Task Submit(Func<Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _scheduler.QueueAsync(work, cancellationToken);
    }

    /// <summary>取消池中所有尚未开始的任务。</summary>
    public void CancelAll() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>
/// 双线程池：CPU 密集与 IO 密集各用一套并发上限。
///
/// ⚠ 总并发数是 <b>2 × MaxThread</b>（两个池各一个）。
/// </summary>
public sealed class DualThreadPool : IDisposable
{
    public int MaxThread { get; }

    private readonly LimitedConcurrencyTaskScheduler _ioScheduler;
    private readonly LimitedConcurrencyTaskScheduler _cpuScheduler;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public DualThreadPool(int maxThread = 2)
    {
        if (maxThread < 1) throw new ArgumentOutOfRangeException(nameof(maxThread));
        MaxThread = maxThread;
        _ioScheduler = new LimitedConcurrencyTaskScheduler(maxThread);
        _cpuScheduler = new LimitedConcurrencyTaskScheduler(maxThread);
    }

    /// <summary>提交一段 IO 密集工作。</summary>
    public Task QueueIo(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _ioScheduler.QueueAsync(() => { work(); return Task.CompletedTask; }, _cts.Token);
    }

    /// <summary>提交一段异步 IO 密集工作。</summary>
    public Task QueueIo(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _ioScheduler.QueueAsync(work, _cts.Token);
    }

    /// <summary>提交一段 CPU 密集工作。</summary>
    public Task QueueCpu(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _cpuScheduler.QueueAsync(() => { work(); return Task.CompletedTask; }, _cts.Token);
    }

    /// <summary>提交一段异步 CPU 密集工作。</summary>
    public Task QueueCpu(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _cpuScheduler.QueueAsync(work, _cts.Token);
    }

    /// <summary>取消两个池中所有尚未开始的任务。</summary>
    public void CancelAll() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
