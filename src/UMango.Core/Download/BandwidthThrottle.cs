namespace UMango.Core.Download;

/// <summary>
/// 全局带宽限速器。
///
/// PCL-CE 把限速挂在全局静态的 `DownloadResourceManager` 上（所有下载共享一份额度）。
/// 这里保持同样语义，但做成**可注入的实例**，避免多窗口互相影响、也便于测试。
///
/// 实现：令牌桶。令牌按时间匀速补充，消费时不足则等待。
/// </summary>
public interface IBandwidthThrottle
{
    /// <summary>消费 <paramref name="byteCount"/> 字节的额度；不足时等待。</summary>
    Task ThrottleAsync(int byteCount, CancellationToken ct = default);

    /// <summary>当前限速上限（字节/秒）。0 表示不限速。</summary>
    long LimitBytesPerSecond { get; }
}

/// <summary>令牌桶限速器。</summary>
public sealed class BandwidthThrottle : IBandwidthThrottle
{
    private readonly long _limit;
    private readonly Lock _gate = new();
    private double _available;
    private long _lastRefillTimestamp;

    public BandwidthThrottle(long limitBytesPerSecond)
    {
        _limit = Math.Max(0, limitBytesPerSecond);
        _available = _limit;
        _lastRefillTimestamp = DateTime.UtcNow.Ticks;
    }

    public long LimitBytesPerSecond => _limit;

    /// <summary>不限速的实例（限速为 0 时直接放行，不做任何等待）。</summary>
    public static BandwidthThrottle Unthrottled { get; } = new(0);

    public async Task ThrottleAsync(int byteCount, CancellationToken ct = default)
    {
        if (_limit <= 0) return;
        if (byteCount <= 0) return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_gate)
            {
                Refill();
                if (_available >= byteCount)
                {
                    _available -= byteCount;
                    return;
                }

                // 缺多少等多久
                var missing = byteCount - _available;
                wait = TimeSpan.FromSeconds(missing / _limit);
                // 直接把缺的额度预扣，避免多个 worker 重复等待同一段时间
                _available = 0;
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsedSeconds = (now - _lastRefillTimestamp) / (double)TimeSpan.TicksPerSecond;
        if (elapsedSeconds <= 0) return;
        _lastRefillTimestamp = now;
        _available = Math.Min(_limit, _available + elapsedSeconds * _limit);
    }
}
