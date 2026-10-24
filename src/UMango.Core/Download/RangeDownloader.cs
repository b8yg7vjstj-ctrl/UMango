using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;

namespace UMango.Core.Download;

/// <summary>
/// 分段（Range）下载器 —— 为支持 HTTP Range 的大文件做**多连接分片 + 慢连接恢复**。
///
/// 这是 PCL-CE `Modules/Network/Downloader/AdaptiveRangeDownloader.cs`（449 行，
/// 受限许可目录）的**等价重写**：只借鉴行为语义，不复制代码。
///
/// 核心行为：
///   · 小文件（小于 4 MB）直接走顺序下载，不做 Range 探测（省一次往返）；
///   · 大文件先发一个 `Range: bytes=0-0` 探测总长度，服务器不支持 Range 就整体降级；
///   · 按 8 MB 切成若干段，用 `Channel` 派发给固定数量的 worker 并发拉取；
///   · 某段明显慢于其它段时**拆分**该段（而不是整段重试），并重试（最多 2 次）；
///   · 所有字节写到文件的**固定偏移**（先 `SetLength`），所以乱序完成也不影响最终文件；
///   · 速度限流是**全局**的（所有 worker 共享一个令牌桶），不是每连接各限一份。
///
/// 与原版的差异：
///   1. 原版用 `SafeFileHandle` + `RandomAccess`（需要暴露文件句柄）；这里用
///      `FileStream` + `Seek`/`WriteAsync`，语义相同但不必暴露句柄，跨平台更省事。
///   2. 原版的限流挂在全局静态 `DownloadResourceManager` 上；这里限流器是**注入**的，
///      便于测试，也避免多窗口互相影响。
///   3. 原版的"慢段判定"散落在会话里；这里把它做成纯函数
///      （<see cref="SegmentSpeedPolicy"/>），可单独测试。
/// </summary>
public sealed class RangeDownloader
{
    /// <summary>小于此大小直接顺序下载，不做 Range 探测。</summary>
    public const long SmallFileThreshold = 4L * 1024 * 1024;

    /// <summary>每段的目标大小。</summary>
    public const long TargetSegmentSize = 8L * 1024 * 1024;

    /// <summary>段数上限（初始切分）。</summary>
    public const int MaxSegmentCount = 1024;

    /// <summary>段数上限（含慢段拆分产生的额外段）。</summary>
    public const int MaxExpandedSegmentCount = MaxSegmentCount * 2;

    private const int BufferSize = 64 * 1024;
    private const int ReadTimeoutMilliseconds = 15_000;
    private const int MaxSegmentRetries = 2;

    /// <summary>慢速判定前至少要观察这么久（秒）——太短的样本没有意义。</summary>
    private const int SlowCheckSeconds = 8;

    /// <summary>剩余字节少于此值时不做慢段重启（重启不划算）。</summary>
    private const long MinimumRestartRemaining = 1L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly RangeDownloaderOptions _options;
    private readonly Action<string>? _log;

    public RangeDownloader(HttpClient http, RangeDownloaderOptions? options = null, Action<string>? log = null)
    {
        _http = http;
        _options = options ?? new RangeDownloaderOptions();
        _log = log;
    }

    /// <summary>
    /// 尝试用分段方式下载。
    /// 返回 true 表示已完成（文件写好）；返回 false 表示**不适合分段**（或服务器不支持
    /// Range），调用方应回退到顺序下载。
    /// </summary>
    public async Task<bool> TryDownloadAsync(
        string url,
        string localPath,
        Action<DownloadProgress>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
        => await TryDownloadAsync(url, localPath, -1, onProgress, onLog, ct).ConfigureAwait(false);

    /// <summary>
    /// 同上，但可预先告知总长度（&gt;= 0）。已知大小时**跳过 Range 探测**，
    /// 省一次往返（与 PCL-CE 原版行为一致）。
    /// </summary>
    public async Task<bool> TryDownloadAsync(
        string url,
        string localPath,
        long expectedSize,
        Action<DownloadProgress>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var log = onLog ?? _log;

        long totalSize;
        if (expectedSize >= 0)
        {
            if (expectedSize < SmallFileThreshold)
                return false;
            totalSize = expectedSize;
        }
        else
        {
            var size = await ProbeAsync(url, ct).ConfigureAwait(false);
            if (size is not { } probed || probed < SmallFileThreshold)
                return false;
            totalSize = probed;
        }

        try
        {
            await DownloadAsync(url, localPath, totalSize, onProgress, log, ct).ConfigureAwait(false);
            return true;
        }
        catch (RangeNotSupportedException ex)
        {
            log?.Invoke($"[下载] 服务器不支持可靠的 Range，改用顺序下载：{ex.Message}");
            return false;
        }
    }

    /// <summary>探测服务器是否支持 Range，并取回总长度。返回 null 表示不支持。</summary>
    private async Task<long?> ProbeAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            return null;

        var range = response.Content.Headers.ContentRange;
        if (range?.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is not > 0)
            return null;

        return range.Length.Value;
    }

    private async Task DownloadAsync(
        string url,
        string localPath,
        long totalSize,
        Action<DownloadProgress>? onProgress,
        Action<string>? log,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(localPath);
        Directory.CreateDirectory(string.IsNullOrEmpty(dir) ? "." : dir);

        var segments = Channel.CreateUnbounded<Segment>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        var segmentCount = EnqueueInitialSegments(segments.Writer, totalSize);
        var workerCount = Math.Min(segmentCount, Math.Clamp(_options.MaxConnections, 1, 32));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using (output.ConfigureAwait(false))
        {
            output.SetLength(totalSize);

            var session = new DownloadSession(this, url, segments, totalSize, segmentCount, output, linked, log, onProgress);

            var workers = Enumerable.Range(0, workerCount).Select(_ => session.RunWorkerAsync()).ToArray();
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                segments.Writer.TryComplete();
            }

            if (session.DownloadedBytes != totalSize)
                throw new IOException($"分段下载不完整：已写入 {session.DownloadedBytes}，应为 {totalSize}");
        }

        onProgress?.Invoke(new DownloadProgress(1.0, totalSize, totalSize, null, null, null));
    }

    private int EnqueueInitialSegments(ChannelWriter<Segment> writer, long totalSize)
    {
        var count = (int)Math.Clamp((totalSize + TargetSegmentSize - 1) / TargetSegmentSize, 1, MaxSegmentCount);
        var segmentSize = (totalSize + count - 1) / count;
        var id = 0;
        for (long start = 0; start < totalSize; start += segmentSize)
        {
            var end = Math.Min(totalSize - 1, start + segmentSize - 1);
            writer.TryWrite(new Segment(Interlocked.Increment(ref id), start, end));
        }
        return id;
    }

    internal HttpRequestMessage CreateSegmentRequest(string url, long from, long to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return request;
    }

    /// <summary>一段下载任务。</summary>
    internal sealed class Segment(int id, long start, long end)
    {
        public int Id { get; } = id;
        public long Start { get; } = start;
        public long End { get; set; } = end;
        public long Downloaded { get; set; }
        public int FailureCount { get; set; }
        public long LastRateSampleTick { get; set; }
        public long Remaining => End - Start - Downloaded + 1;
        public long CurrentOffset => Start + Downloaded;
    }

    /// <summary>下载会话：持有共享状态，驱动 worker。</summary>
    private sealed class DownloadSession
    {
        private readonly RangeDownloader _owner;
        private readonly string _url;
        private readonly Channel<Segment> _segments;
        private readonly long _totalSize;
        private readonly FileStream _output;
        private readonly CancellationTokenSource _cancellation;
        private readonly Action<string>? _log;
        private readonly Action<DownloadProgress>? _onProgress;

        private readonly ConcurrentDictionary<int, RateSample> _rates = new();
        private readonly ConcurrentQueue<double> _completedRates = new();
        private readonly object _progressLock = new();

        private int _outstandingSegments;
        private int _nextSegmentId;
        private int _activeConnections;
        private long _downloadedBytes;
        private long _lastProgressBytes;
        private long _lastProgressTick = Stopwatch.GetTimestamp();
        private Exception? _failure;

        public DownloadSession(
            RangeDownloader owner,
            string url,
            Channel<Segment> segments,
            long totalSize,
            int segmentCount,
            FileStream output,
            CancellationTokenSource cancellation,
            Action<string>? log,
            Action<DownloadProgress>? onProgress)
        {
            _owner = owner;
            _url = url;
            _segments = segments;
            _totalSize = totalSize;
            _output = output;
            _cancellation = cancellation;
            _log = log;
            _onProgress = onProgress;
            _outstandingSegments = segmentCount;
            _nextSegmentId = segmentCount;
        }

        public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

        public async Task RunWorkerAsync()
        {
            try
            {
                while (await _segments.Reader.WaitToReadAsync(_cancellation.Token).ConfigureAwait(false))
                {
                    while (_segments.Reader.TryRead(out var segment))
                    {
                        try
                        {
                            await DownloadSegmentAsync(segment, _cancellation.Token).ConfigureAwait(false);
                            if (Interlocked.Decrement(ref _outstandingSegments) == 0)
                                _segments.Writer.TryComplete();
                        }
                        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (TryRecover(segment, ex))
                            {
                                _log?.Invoke($"[下载] 分段 {segment.Id} 失败或过慢，正在重试：{ex.Message}");
                                await Task.Delay(Random.Shared.Next(250, 1001), _cancellation.Token).ConfigureAwait(false);
                                continue;
                            }

                            Interlocked.CompareExchange(ref _failure, ex, null);
                            _segments.Writer.TryComplete(ex);
                            _cancellation.Cancel();
                            throw;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_failure is not null)
            {
                throw _failure;
            }
        }

        /// <summary>
        /// 尝试恢复一个失败的段。
        ///
        /// 关键行为：若该段还剩很多字节，**把它拆成两半**再排回队列
        /// —— 这样慢连接只是变成两个更短的段，而不是整段重来。
        /// </summary>
        private bool TryRecover(Segment segment, Exception exception)
        {
            if (exception is not (SlowSegmentException or IOException or HttpRequestException or TaskCanceledException))
                return false;
            if (++segment.FailureCount > MaxSegmentRetries || segment.Remaining <= 0)
                return false;

            if (segment.Remaining >= _owner._options.SlowSplitThreshold &&
                Volatile.Read(ref _nextSegmentId) < MaxExpandedSegmentCount)
            {
                var splitStart = segment.CurrentOffset + segment.Remaining / 2;
                var split = new Segment(Interlocked.Increment(ref _nextSegmentId), splitStart, segment.End);
                segment.End = splitStart - 1;
                _segments.Writer.TryWrite(segment);
                _segments.Writer.TryWrite(split);
                Interlocked.Increment(ref _outstandingSegments);
            }
            else
            {
                _segments.Writer.TryWrite(segment);
            }

            return true;
        }

        private async Task DownloadSegmentAsync(Segment segment, CancellationToken ct)
        {
            Interlocked.Increment(ref _activeConnections);
            var startedAt = Stopwatch.GetTimestamp();
            var attemptBytes = 0L;
            try
            {
                using var request = _owner.CreateSegmentRequest(_url, segment.CurrentOffset, segment.End);
                using var response = await _owner._http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                ValidateRangeResponse(response, segment, _totalSize);

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var buffer = new byte[BufferSize];

                while (segment.Remaining > 0)
                {
                    int read;
                    readTimeout.CancelAfter(ReadTimeoutMilliseconds);
                    try
                    {
                        read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), readTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && readTimeout.IsCancellationRequested)
                    {
                        throw new SlowSegmentException("分段在等待数据时超时");
                    }
                    finally
                    {
                        readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    }

                    if (read == 0) break;
                    read = (int)Math.Min(read, segment.Remaining);

                    if (_owner._options.Throttle is { } throttle)
                        await throttle.ThrottleAsync(read, ct).ConfigureAwait(false);

                    _output.Seek(segment.CurrentOffset, SeekOrigin.Begin);
                    await _output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    segment.Downloaded += read;
                    attemptBytes += read;
                    var downloaded = Interlocked.Add(ref _downloadedBytes, read);
                    ReportRateAndProgress(segment, attemptBytes, startedAt, downloaded);
                }

                if (segment.Remaining > 0)
                    throw new IOException($"分段 {segment.Id} 提前结束，还剩 {segment.Remaining} 字节");

                AddCompletedRate(attemptBytes, startedAt);
            }
            finally
            {
                _rates.TryRemove(segment.Id, out _);
                Interlocked.Decrement(ref _activeConnections);
                ReportProgress(Interlocked.Read(ref _downloadedBytes), Volatile.Read(ref _activeConnections), true);
            }
        }

        private void ReportRateAndProgress(Segment segment, long attemptBytes, long startedAt, long downloaded)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedSeconds = Math.Max(0.001, (double)(now - startedAt) / Stopwatch.Frequency);
            if (now - segment.LastRateSampleTick >= Stopwatch.Frequency / 2)
            {
                segment.LastRateSampleTick = now;
                var ownRate = attemptBytes / elapsedSeconds;
                _rates[segment.Id] = new RateSample(ownRate, now);

                if (IsSignificantlySlow(segment, ownRate, startedAt, now))
                    throw new SlowSegmentException("分段速度明显低于其他活跃连接");
            }

            ReportProgress(downloaded, Volatile.Read(ref _activeConnections));
        }

        /// <summary>
        /// 慢段判定（与 PCL-CE 原版同语义）。
        ///
        /// 三条守卫缺一不可：
        ///   1. **设了限速**时不做判定 —— 限速下所有段都慢，重启没有意义；
        ///   2. **观察时间不足** 8 秒时不做判定 —— 样本太短会误判；
        ///   3. 段**剩余不足 1 MB** 时不做判定 —— 拆了也不划算。
        ///
        /// 比较时**排除自己**的速率（否则自己的低速率会拉高中位数，把自己掩盖掉）。
        /// </summary>
        private bool IsSignificantlySlow(Segment segment, double ownRate, long startedAt, long now)
        {
            if (_owner._options.Throttle is not null)
                return false;
            if (segment.Remaining < MinimumRestartRemaining)
                return false;

            var elapsedSeconds = (double)(now - startedAt) / Stopwatch.Frequency;
            if (elapsedSeconds < SlowCheckSeconds)
                return false;

            var otherRates = SampleOtherRates(segment.Id);
            if (otherRates.Length == 0)
                return false;

            return SegmentSpeedPolicy.IsSignificantlySlow(ownRate, otherRates, _owner._options);
        }

        private double[] SampleOtherRates(int excludeSegmentId)
        {
            var now = Stopwatch.GetTimestamp();
            var rates = _rates
                .Where(p => p.Key != excludeSegmentId &&
                            now - p.Value.UpdatedAt <= Stopwatch.Frequency * 2 && p.Value.BytesPerSecond > 0)
                .Select(p => p.Value.BytesPerSecond).ToArray();
            if (rates.Length == 0)
                rates = _completedRates.Where(r => r > 0).ToArray();
            return rates;
        }

        private void AddCompletedRate(long attemptBytes, long startedAt)
        {
            var elapsedSeconds = Math.Max(0.001, (double)(Stopwatch.GetTimestamp() - startedAt) / Stopwatch.Frequency);
            _completedRates.Enqueue(attemptBytes / elapsedSeconds);
            while (_completedRates.Count > 8) _completedRates.TryDequeue(out _);
        }

        private void ReportProgress(long downloaded, int activeConnections, bool force = false)
        {
            lock (_progressLock)
            {
                var now = Stopwatch.GetTimestamp();
                if (!force && now - _lastProgressTick < Stopwatch.Frequency / 5) return;

                var elapsed = Math.Max(1L, now - _lastProgressTick);
                var speed = Math.Max(0L, (long)((downloaded - _lastProgressBytes) * (double)Stopwatch.Frequency / elapsed));
                _lastProgressBytes = downloaded;
                _lastProgressTick = now;

                _onProgress?.Invoke(new DownloadProgress(
                    _totalSize > 0 ? (double)downloaded / _totalSize : null,
                    downloaded, _totalSize,
                    FormatSpeed(speed), null, null));
            }
        }

        private static string? FormatSpeed(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return null;
            return bytesPerSecond switch
            {
                < 1024 => $"{bytesPerSecond} B/s",
                < 1024 * 1024 => $"{bytesPerSecond / 1024.0:0.#} KB/s",
                _ => $"{bytesPerSecond / (1024.0 * 1024):0.#} MB/s"
            };
        }

        private void ValidateRangeResponse(HttpResponseMessage response, Segment segment, long totalSize)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new RangeNotSupportedException($"服务器未按 Range 返回分段，状态码：{(int)response.StatusCode}");

            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != segment.CurrentOffset || range.To != segment.End ||
                range.Length != totalSize)
                throw new RangeNotSupportedException("服务器返回的 Content-Range 与请求分段不一致");
        }

        private readonly record struct RateSample(double BytesPerSecond, long UpdatedAt);
    }

    /// <summary>段速度明显偏慢时的异常。</summary>
    internal sealed class SlowSegmentException(string message) : IOException(message);

    /// <summary>服务器不支持可靠的 Range。</summary>
    internal sealed class RangeNotSupportedException(string message) : Exception(message);
}

/// <summary>分段下载的可调参数。</summary>
public sealed class RangeDownloaderOptions
{
    /// <summary>单个文件的最大并发连接数。</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>全局限速器（所有连接共享）。空表示不限速。</summary>
    public IBandwidthThrottle? Throttle { get; init; }

    /// <summary>段剩余字节数达到此值才允许被拆分。</summary>
    public long SlowSplitThreshold { get; init; } = 2L * 1024 * 1024;

    /// <summary>绝对慢速阈值（字节/秒）。</summary>
    public long SlowSpeedBytesPerSecond { get; init; } = 50L * 1024;

    /// <summary>「明显更快」阈值（字节/秒）。</summary>
    public long ClearlyFastBytesPerSecond { get; init; } = 128L * 1024;
}

/// <summary>
/// 慢段判定（纯函数，便于单测）。
///
/// 规则（与 PCL-CE 原版一致）：
///   · 相对慢：其它段速度中位数明显更快，且自己不到其 1/4；
///   · 绝对慢：自己低于绝对阈值，且**存在**一个明显更快的段
///     —— 这一条"必须存在明显更快段"很关键：否则所有连接都被同一个源限速时，
///     会导致所有段一起反复重启。
/// </summary>
public static class SegmentSpeedPolicy
{
    public static bool IsSignificantlySlow(
        double ownRate,
        IReadOnlyList<double> otherRates,
        RangeDownloaderOptions options)
    {
        if (otherRates.Count == 0) return false;

        var sorted = otherRates.OrderBy(r => r).ToArray();
        var median = sorted[sorted.Length / 2];
        var fastest = sorted[^1];

        var hasClearlyFaster = fastest >= options.ClearlyFastBytesPerSecond && ownRate * 4 < fastest;
        var relativelySlow = median >= options.ClearlyFastBytesPerSecond && ownRate * 4 < median;
        var absolutelySlow = ownRate < options.SlowSpeedBytesPerSecond && hasClearlyFaster;
        return relativelySlow || absolutelySlow;
    }
}
