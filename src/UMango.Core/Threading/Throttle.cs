namespace UMango.Core.Threading;

/// <summary>
/// 节流：确保操作之间至少间隔 minDelayMs。
///
/// 使用场景：刷新包源——用户连续点击"刷新"时，
/// 不应该每次都重新起六个进程，而是"间隔 2 秒才执行下一次"。
///
/// 与 PCL-CE 的 `PCL.Core/Utils/Threading/AsyncThrottle` 对应（Apache-2.0）。
/// 这里用更简单的锁实现。
/// </summary>
public sealed class Throttle
{
    private readonly int _minDelayMs;
    private DateTime _lastInvocation = DateTime.MinValue;
    private readonly Lock _gate = new();

    public Throttle(int minDelayMs = 2000) => _minDelayMs = Math.Max(1, minDelayMs);

    /// <summary>
    /// 如果距上次执行不足 minDelayMs，等待到足够间隔后再执行；
    /// 否则立即执行。返回 true 表示实际执行了，false 表示被节流跳过。
    /// </summary>
    public async Task<bool> InvokeAsync(Func<Task> action, CancellationToken ct = default)
    {
        DateTime readyAt;
        lock (_gate)
        {
            readyAt = _lastInvocation.AddMilliseconds(_minDelayMs);
            var wait = readyAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _lastInvocation = DateTime.UtcNow;
                return false;
            }
            _lastInvocation = DateTime.UtcNow;
        }

        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            throw;
        }
    }
}
