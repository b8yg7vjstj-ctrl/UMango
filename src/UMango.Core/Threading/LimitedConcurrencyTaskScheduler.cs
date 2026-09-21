namespace UMango.Core.Threading;

/// <summary>
/// 有并发上限的任务调度器。
///
/// 使用场景：六个包管理器同时探测 CLI 时，若不限制并发，
/// 可能同时起 6 个进程（每个进程还可能起子进程），
/// 在配置低的机器上导致系统卡顿或触发文件系统锁竞争。
///
/// 与 PCL-CE 的 `PCL.Core/Utils/Threading/LimitedConcurrencyLevelTaskScheduler`
/// 对应（Apache-2.0，46 行）。这里用更简单的信号量实现，
/// 行为等价：同时运行的任务不超过 maxConcurrency 个。
/// </summary>
public sealed class LimitedConcurrencyTaskScheduler
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TaskScheduler _taskScheduler;

    public LimitedConcurrencyTaskScheduler(int maxConcurrency = 4)
    {
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _taskScheduler = TaskScheduler.Default;
    }

    public int MaxConcurrency => _semaphore.CurrentCount;

    /// <summary>
    /// 提交一个任务到有并发上限的队列。
    /// 若已达上限，Task 会在信号量释放后才开始执行。
    /// </summary>
    public Task QueueAsync(Func<Task> action, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }, ct);
    }

    /// <summary>当前正在运行的任务数。</summary>
    public int Running => _semaphore.CurrentCount < _semaphore.Release(0) ? _semaphore.Release(0) : 0;
}
