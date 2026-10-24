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
    private readonly int _maxConcurrency;
    private readonly TaskScheduler _taskScheduler;
    private readonly Func<CancellationToken, Task> _acquire;
    private readonly bool _usesSemaphore;

    /// <param name="maxConcurrency">并发上限</param>
    /// <param name="acquire">
    /// 取得"执行槽位"的方式。默认用信号量。
    /// 暴露这个参数是为了**测试**：信号量在"许可被释放"与"取消信号生效"
    /// 同时到达的那一刻会授予许可而不是抛取消（实测同时到达时约 82% 会授予），
    /// 而这个窗口靠打时间差是测不准的 —— 测试需要一个能**确定地**
    /// 制造出"已授予槽位但令牌已取消"状态的入口。
    /// </param>
    public LimitedConcurrencyTaskScheduler(int maxConcurrency = 4, Func<CancellationToken, Task>? acquire = null)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _taskScheduler = TaskScheduler.Default;
        _acquire = acquire ?? (Func<CancellationToken, Task>)(async ct => await _semaphore.WaitAsync(ct).ConfigureAwait(false));
        _usesSemaphore = acquire is null;
    }

    /// <summary>并发上限。</summary>
    public int MaxConcurrency => _maxConcurrency;

    /// <summary>
    /// 提交一个任务到有并发上限的队列。
    /// 若已达上限，Task 会在信号量释放后才开始执行。
    /// </summary>
    public Task QueueAsync(Func<Task> action, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            await _acquire(ct).ConfigureAwait(false);
            try
            {
                // 取得槽位与取消信号生效之间存在竞态：
                // 恰好有一个槽位被释放时，取得槽位的一方可能成功拿到它
                // 而不是看到取消。若不在这里补一刀，被取消的任务仍会执行它的
                // 委托 —— 表现出来就是"点了取消，包管理器还是跑起来了"。
                ct.ThrowIfCancellationRequested();

                await action().ConfigureAwait(false);
            }
            finally
            {
                // 自定义 acquire 时并没有真的占信号量，释放它会多放行一个任务
                if (_usesSemaphore) _semaphore.Release();
            }
        }, ct);
    }

    /// <summary>当前正在运行的任务数。</summary>
    public int Running => _maxConcurrency - _semaphore.CurrentCount;
}
