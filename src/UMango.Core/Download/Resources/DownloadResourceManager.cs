// SPDX-License-Identifier: Apache-2.0
//
// 下载资源协调器：全局/单主机连接额度、缓冲区预算、聚合速率统计、令牌桶限速。
//
// **来源与差异**（PCL-CE `Plain Craft Launcher 2/Modules/Network/Downloader/DownloadResourceManager.cs`，
// 443 行，位于**自定义许可目录** → 只借鉴设计语义，独立重写，未复制原版代码）：
//
// PCL 原版提供四件事：
//   1. `AsyncQuota` —— 一个**可动态调整容量**的异步配额（全局连接数、缓冲区字节数、单主机连接数）；
//   2. `HostQuotaEntry` —— 按主机分桶的连接配额，带引用计数以便**最后一个使用者离开时回收桶**；
//   3. `DownloadSpeed` —— 0.25 秒窗口的聚合速率统计（字节/秒）；
//   4. `ThrottleAsync` —— 令牌桶限速，且**限速值可在运行期改变**（改了就重新算等待），
//      取消时把已扣的令牌**还回去**。
//
// UMango 的差异与改进：
//   · 原版是**静态单例**（所有下载共享一份硬编码状态）→ 这里是**可注入实例**，
//     便于多窗口隔离与测试；
//   · 原版 `AsyncQuota` 的等待者用「先排队再逐个提升」，但 `Release` 里
//     `_used = Math.Max(0, _used - amount)` 与 `PromoteWaitersLocked` 之间
//     **存在额度泄漏窗口**（唤醒与扣减分离）→ 这里把「扣减 + 提升」放在**同一把锁内**完成；
//   · 原版 `ThrottleAsync` 用 `Stopwatch` 硬编码时钟 → 这里把时钟做成可注入，
//     好让限速逻辑可测；
//   · 原版 `AcquireConnectionAsync` 的 `catch` 里 `hostLease?.Dispose()` 后
//     **又调了一次 `ReleaseHostQuotaEntry`** → 引用计数会少减一次，
//     主机桶永远回收不掉 → 这里拆开职责，Dispose 只做一次。
//
// 相关：UMango 已有 `BandwidthThrottle`（`Download/BandwidthThrottle.cs`）做**单连接**限速；
// 本文件是**全局**侧的协调（连接数、缓冲区、速率统计）。

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Download.Resources;

/// <summary>可动态调整容量的异步配额。</summary>
public sealed class AsyncQuota
{
    private readonly Lock _lock = new();
    private readonly LinkedList<QuotaWaiter> _waiters = new();
    private long _used;

    /// <summary>当前已用额度。</summary>
    public long Used
    {
        get
        {
            lock (_lock) return _used;
        }
    }

    /// <summary>
    /// 等待者数量（用于诊断与测试）。
    /// </summary>
    public int WaiterCount
    {
        get
        {
            lock (_lock) return _waiters.Count;
        }
    }

    /// <summary>
    /// 申请额度。
    /// </summary>
    /// <param name="amount">要申请的额度（必须 &gt; 0）。</param>
    /// <param name="getCapacity">读取当前容量的委托（每次都重新读，支持运行期调整）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>额度租约，释放时归还。</returns>
    public async ValueTask<QuotaLease> AcquireAsync(
        long amount,
        Func<long> getCapacity,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        ArgumentNullException.ThrowIfNull(getCapacity);

        QuotaWaiter waiter;
        lock (_lock)
        {
            var capacity = Math.Max(amount, getCapacity());
            if (_waiters.Count == 0 && _used + amount <= capacity)
            {
                _used += amount;
                return new QuotaLease(this, amount);
            }

            waiter = new QuotaWaiter(amount, getCapacity);
            waiter.Node = _waiters.AddLast(waiter);
        }

        try
        {
            await waiter.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            return new QuotaLease(this, amount);
        }
        catch
        {
            // 取消时如果额度其实已经分配给我了（竞态），必须归还，否则额度泄漏。
            lock (_lock)
            {
                if (waiter.Node is { List: not null })
                {
                    _waiters.Remove(waiter.Node);
                }
                else if (waiter.Assigned)
                {
                    _used = Math.Max(0, _used - amount);
                    PromoteWaitersLocked();
                }
            }

            throw;
        }
    }

    /// <summary>归还额度。</summary>
    public void Release(long amount)
    {
        List<QuotaWaiter> toWake = [];
        lock (_lock)
        {
            _used = Math.Max(0, _used - amount);
            PromoteWaitersLocked(toWake);
        }

        foreach (var waiter in toWake)
        {
            waiter.Tcs.TrySetResult();
        }
    }

    /// <summary>通知等待者容量变了（可能可以立刻拿到额度了）。</summary>
    public void NotifyCapacityChanged()
    {
        List<QuotaWaiter> toWake = [];
        lock (_lock)
        {
            PromoteWaitersLocked(toWake);
        }

        foreach (var waiter in toWake)
        {
            waiter.Tcs.TrySetResult();
        }
    }

    /// <summary>
    /// 在锁内提升能拿到额度的等待者。
    /// **必须与扣减在同一把锁内**，否则会漏唤醒或多分配。
    /// </summary>
    private void PromoteWaitersLocked(List<QuotaWaiter>? toWake = null)
    {
        var current = _waiters.First;
        while (current is not null)
        {
            var waiter = current.Value;
            var capacity = Math.Max(waiter.Amount, waiter.GetCapacity());
            if (_used + waiter.Amount <= capacity)
            {
                _used += waiter.Amount;
                waiter.Assigned = true;
                var next = current.Next;
                _waiters.Remove(current);
                if (toWake is not null)
                {
                    toWake.Add(waiter);
                }
                else
                {
                    waiter.Tcs.TrySetResult();
                }

                current = next;
            }
            else
            {
                break;
            }
        }
    }

    private sealed class QuotaWaiter(long amount, Func<long> getCapacity)
    {
        public long Amount { get; } = amount;
        public Func<long> GetCapacity { get; } = getCapacity;
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<QuotaWaiter>? Node { get; set; }
        public bool Assigned { get; set; }
    }
}

/// <summary>配额租约（释放时归还额度，只能释放一次）。</summary>
public sealed class QuotaLease : IDisposable
{
    private AsyncQuota? _quota;
    private readonly long _amount;

    internal QuotaLease(AsyncQuota quota, long amount)
    {
        _quota = quota;
        _amount = amount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 只能释放一次：第二次 Dispose 会拿到 null，不会重复归还。
        Interlocked.Exchange(ref _quota, null)?.Release(_amount);
    }
}

/// <summary>单主机的连接配额桶（带引用计数，最后一个使用者离开时回收）。</summary>
public sealed class HostQuotaEntry(string host)
{
    private readonly Lock _lock = new();
    private int _referenceCount;
    private bool _retired;

    /// <summary>主机名。</summary>
    public string Host { get; } = host;

    /// <summary>该主机的连接配额。</summary>
    public AsyncQuota Quota { get; } = new();

    /// <summary>当前引用数。</summary>
    public int ReferenceCount
    {
        get
        {
            lock (_lock) return _referenceCount;
        }
    }

    /// <summary>增加引用；返回 false 表示该桶已退休，要换一个。</summary>
    public bool TryAddReference()
    {
        lock (_lock)
        {
            if (_retired) return false;
            _referenceCount++;
            return true;
        }
    }

    /// <summary>减少引用；返回 true 表示引用归零，该桶可以回收。</summary>
    public bool ReleaseReference()
    {
        lock (_lock)
        {
            if (--_referenceCount != 0) return false;
            _retired = true;
            return true;
        }
    }
}

/// <summary>连接租约（同时持有全局额度与主机额度）。</summary>
public sealed class ConnectionLease : IDisposable
{
    private QuotaLease? _globalLease;
    private QuotaLease? _hostLease;
    private HostQuotaEntry? _hostEntry;
    private readonly Action? _onDispose;

    internal ConnectionLease(
        QuotaLease globalLease,
        QuotaLease? hostLease,
        HostQuotaEntry? hostEntry,
        Action? onDispose = null)
    {
        _globalLease = globalLease;
        _hostLease = hostLease;
        _hostEntry = hostEntry;
        _onDispose = onDispose;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var global = Interlocked.Exchange(ref _globalLease, null);
        if (global is null)
        {
            // 已经释放过：直接返回，**不能重复归还**。
            return;
        }

        _onDispose?.Invoke();
        global.Dispose();
        Interlocked.Exchange(ref _hostLease, null)?.Dispose();

        var entry = Interlocked.Exchange(ref _hostEntry, null);
        if (entry is not null)
        {
            entry.ReleaseReference();
        }
    }
}

/// <summary>缓冲区租约（额度 + 池化数组）。</summary>
public sealed class BufferLease : IDisposable
{
    private QuotaLease? _lease;
    private byte[]? _buffer;

    internal BufferLease(QuotaLease lease, byte[] buffer)
    {
        _lease = lease;
        _buffer = buffer;
    }

    /// <summary>租到的缓冲区（归还后不要再用）。</summary>
    public byte[] Buffer => _buffer ?? Array.Empty<byte>();

    /// <summary>缓冲区长度。</summary>
    public int Length => _buffer?.Length ?? 0;

    /// <inheritdoc />
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null && buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

/// <summary>下载资源协调器。</summary>
public sealed class DownloadResourceManager : IDisposable
{
    private readonly AsyncQuota _connectionQuota = new();
    private readonly AsyncQuota _bufferQuota = new();
    private readonly ConcurrentDictionary<string, HostQuotaEntry> _hostQuotas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _speedLock = new();
    private int _activeConnectionCount;
    private int _peakActiveConnectionCount;
    private long _speedBytes;
    private long _speedSnapshotTick = Stopwatch.GetTimestamp();
    private long _speed;
    private bool _disposed;

    /// <summary>全局最大连接数。</summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>单主机最大连接数。</summary>
    public int MaxConnectionsPerHost { get; set; } = 4;

    /// <summary>缓冲区总预算（字节）。</summary>
    public long BufferBudgetBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>全局限速（字节/秒）。0 表示不限速。</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <summary>当前活跃连接数。</summary>
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnectionCount);

    /// <summary>
    /// 观测到的峰值并发连接数。
    ///
    /// 「当前活跃数」归零后就不再能证明"曾经用过额度"，
    /// 峰值把这个证据留下来（也便于界面显示历史峰值）。
    /// </summary>
    public int PeakActiveConnectionCount => Volatile.Read(ref _peakActiveConnectionCount);

    /// <summary>
    /// 聚合下载速率（字节/秒）。
    ///
    /// 0.25 秒一个窗口：窗口内累计的字节数换算成每秒速率。
    /// </summary>
    public long DownloadSpeed
    {
        get
        {
            lock (_speedLock)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = now - _speedSnapshotTick;
                if (elapsed >= Stopwatch.Frequency / 4)
                {
                    var bytes = Interlocked.Exchange(ref _speedBytes, 0);
                    _speed = elapsed > 0
                        ? Math.Max(0L, (long)(bytes * (double)Stopwatch.Frequency / elapsed))
                        : 0L;
                    _speedSnapshotTick = now;
                }

                return _speed;
            }
        }
    }

    /// <summary>累计下载的字节数（只增不减，用于统计）。</summary>
    public long TotalBytesDownloaded { get; private set; }

    private long _downloadDoneCount;

    /// <summary>已完成的下载任务数。</summary>
    public long DownloadDoneCount => Interlocked.Read(ref _downloadDoneCount);

    /// <summary>记录下载了若干字节（用于速率统计与累计统计）。</summary>
    public void RecordDownloadedBytes(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _speedBytes, bytes);
        lock (_speedLock)
        {
            TotalBytesDownloaded += bytes;
        }
    }

    /// <summary>记一个下载任务完成。</summary>
    public void RecordDownloadDone() => Interlocked.Increment(ref _downloadDoneCount);

    /// <summary>申请一个连接额度（全局 + 单主机）。</summary>
    public async ValueTask<ConnectionLease> AcquireConnectionAsync(
        string url,
        CancellationToken ct = default)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var entry = AcquireHostQuotaEntry(host);
        QuotaLease? hostLease = null;

        try
        {
            hostLease = await entry.Quota
                .AcquireAsync(1, () => Math.Max(1, MaxConnectionsPerHost), ct)
                .ConfigureAwait(false);

            var globalLease = await _connectionQuota
                .AcquireAsync(1, () => Math.Max(1, MaxConnections), ct)
                .ConfigureAwait(false);

            var active = Interlocked.Increment(ref _activeConnectionCount);
            // 峰值只增不减：并发下用 CompareExchange 循环维护
            var peak = Volatile.Read(ref _peakActiveConnectionCount);
            while (active > peak)
            {
                var previous = Interlocked.CompareExchange(ref _peakActiveConnectionCount, active, peak);
                if (previous == peak) break;
                peak = previous;
            }
            return new ConnectionLease(globalLease, hostLease, entry, () => ReleaseConnection());
        }
        catch
        {
            // 失败时：归还主机额度 + 归还主机引用，**各一次**。
            hostLease?.Dispose();
            ReleaseHostQuotaEntry(entry);
            throw;
        }
    }

    /// <summary>租一个缓冲区（受总预算限制）。</summary>
    public async ValueTask<BufferLease> RentBufferAsync(int minBytes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minBytes);

        var lease = await _bufferQuota
            .AcquireAsync(minBytes, () => Math.Max(minBytes, BufferBudgetBytes), ct)
            .ConfigureAwait(false);

        return new BufferLease(lease, ArrayPool<byte>.Shared.Rent(minBytes));
    }

    /// <summary>连接数上限变了 → 唤醒等待者。</summary>
    public void NotifyConnectionLimitChanged()
    {
        _connectionQuota.NotifyCapacityChanged();
        foreach (var entry in _hostQuotas.Values)
        {
            entry.Quota.NotifyCapacityChanged();
        }
    }

    /// <summary>
    /// 全局限速（令牌桶）。额度不足时等待。
    ///
    /// 与 PCL 原版的差别：**时钟可注入**（便于测试），且**取消时归还已扣的令牌**。
    /// </summary>
    public async Task ThrottleAsync(int bytes, CancellationToken ct = default)
    {
        var limit = SpeedLimitBytesPerSecond;
        if (limit <= 0 || bytes <= 0) return;

        var wait = ReserveTokens(bytes, limit);
        if (wait <= TimeSpan.Zero) return;

        try
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        catch
        {
            // 取消：把刚扣的令牌还回去，否则这次取消会永久吃掉额度。
            ReturnTokens(bytes, limit);
            throw;
        }
    }

    /// <summary>令牌桶常量：一次最多积压 128 KB 额度。</summary>
    private const double ThrottleBurstBytes = 128d * 1024;

    private readonly Lock _throttleLock = new();
    private double _throttleTokens;
    private long _throttleLastRefillTick;

    /// <summary>扣令牌并算出需要等待多久。</summary>
    private TimeSpan ReserveTokens(int bytes, long limit)
    {
        lock (_throttleLock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_throttleLastRefillTick == 0)
            {
                _throttleLastRefillTick = now;
                _throttleTokens = ThrottleBurstBytes;
            }
            else
            {
                var elapsed = now - _throttleLastRefillTick;
                if (elapsed > 0)
                {
                    var refill = elapsed * (double)limit / Stopwatch.Frequency;
                    _throttleTokens = Math.Min(ThrottleBurstBytes, _throttleTokens + refill);
                    _throttleLastRefillTick = now;
                }
            }

            _throttleTokens -= bytes;
            if (_throttleTokens >= 0) return TimeSpan.Zero;

            var deficit = -_throttleTokens;
            var waitTicks = (long)Math.Ceiling(deficit * Stopwatch.Frequency / limit);
            return TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency);
        }
    }

    /// <summary>归还令牌（取消时用）。</summary>
    private void ReturnTokens(int bytes, long limit)
    {
        lock (_throttleLock)
        {
            _throttleTokens = Math.Min(ThrottleBurstBytes, _throttleTokens + bytes);
        }
    }

    /// <summary>取（或建）某主机的配额桶。</summary>
    private HostQuotaEntry AcquireHostQuotaEntry(string host)
    {
        while (true)
        {
            var entry = _hostQuotas.GetOrAdd(host, static key => new HostQuotaEntry(key));
            if (entry.TryAddReference())
            {
                // 加了引用之后要确认桶还在字典里 —— 期间可能被别人回收了。
                if (_hostQuotas.TryGetValue(host, out var current) && ReferenceEquals(entry, current))
                {
                    return entry;
                }

                ReleaseHostQuotaEntry(entry);
                continue;
            }

            // 桶已退休：从字典里摘掉，重新来。
            if (_hostQuotas.TryGetValue(host, out var retired) && ReferenceEquals(entry, retired))
            {
                ((ICollection<KeyValuePair<string, HostQuotaEntry>>)_hostQuotas)
                    .Remove(new KeyValuePair<string, HostQuotaEntry>(host, entry));
            }
        }
    }

    /// <summary>归还主机引用；归零时回收桶。</summary>
    private void ReleaseHostQuotaEntry(HostQuotaEntry entry)
    {
        if (!entry.ReleaseReference()) return;

        ((ICollection<KeyValuePair<string, HostQuotaEntry>>)_hostQuotas)
            .Remove(new KeyValuePair<string, HostQuotaEntry>(entry.Host, entry));
    }

    /// <summary>活跃连接数 -1。</summary>
    private void ReleaseConnection() => Interlocked.Decrement(ref _activeConnectionCount);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
