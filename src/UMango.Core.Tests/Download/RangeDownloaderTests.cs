using System.Net;
using System.Net.Sockets;
using System.Text;
using UMango.Core.Download;
using Xunit;

namespace UMango.Core.Tests.Download;

/// <summary>
/// 分段下载器的端到端测试。
///
/// 用一个**真实的本地 HTTP 服务器**（TcpListener 手写，精确控制 Range 行为），
/// 因为这个类的核心就是与服务器的协商 —— 用 mock 的 HttpMessageHandler 测不出
/// "服务器返回的 Content-Range 与请求不一致" 这类真实故障。
/// </summary>
public sealed class RangeDownloaderTests
{
    // ---------- 慢段判定（纯函数） ----------

    [Fact]
    public void 慢段判定_没有其它段时不判慢()
    {
        var options = new RangeDownloaderOptions();
        Assert.False(SegmentSpeedPolicy.IsSignificantlySlow(1, [], options));
    }

    [Fact]
    public void 慢段判定_自己比其它段快得多时不判慢()
    {
        var options = new RangeDownloaderOptions();
        Assert.False(SegmentSpeedPolicy.IsSignificantlySlow(1_000_000, [100, 100], options));
    }

    [Fact]
    public void 慢段判定_明显更慢时判慢()
    {
        var options = new RangeDownloaderOptions();
        // 自己 1 KB/s，其它 1 MB/s
        Assert.True(SegmentSpeedPolicy.IsSignificantlySlow(1024, [1_000_000, 1_000_000], options));
    }

    [Fact]
    public void 慢段判定_所有段都被源限速时都不判慢()
    {
        var options = new RangeDownloaderOptions();
        // 全部只有 10 KB/s（同一个源限速）—— 不能互相判定为慢，否则会无限重启
        Assert.False(SegmentSpeedPolicy.IsSignificantlySlow(10_000, [10_000, 10_000], options));
    }

    [Fact]
    public void 慢段判定_只有一个明显更快的段时才用绝对阈值()
    {
        var options = new RangeDownloaderOptions();
        // 自己 1 KB/s（低于绝对阈值 50KB/s），但没有明显更快的段
        Assert.False(SegmentSpeedPolicy.IsSignificantlySlow(1024, [1024, 1024], options));
        // 出现了一个 1 MB/s 的段 -> 判慢
        Assert.True(SegmentSpeedPolicy.IsSignificantlySlow(1024, [1024, 1_000_000], options));
    }

    [Fact]
    public void 慢段判定_中位数与最快值都参与判断()
    {
        var options = new RangeDownloaderOptions();
        // 3 个其它段：1 慢 + 2 快 -> 中位数是快
        Assert.True(SegmentSpeedPolicy.IsSignificantlySlow(1000, [1000, 1_000_000, 1_000_000], options));
    }

    // ---------- 端到端 ----------

    [Fact]
    public async Task 小文件_不做分段直接返回false()
    {
        await using var server = await TestServer.Start();
        server.Content = RandomBytes(1024);

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();

        var done = await downloader.TryDownloadAsync(server.Url, path);
        Assert.False(done);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 服务器不支持Range时返回false并可回退()
    {
        await using var server = await TestServer.Start(supportRange: false);
        server.Content = RandomBytes(1024 * 1024);

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();

        var done = await downloader.TryDownloadAsync(server.Url, path);
        Assert.False(done);
    }

    [Fact]
    public async Task 大文件_分段下载后内容与源一致()
    {
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 300_000);
        await using var server = await TestServer.Start();
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var done = await downloader.TryDownloadAsync(server.Url, path);
            Assert.True(done);
            var written = await File.ReadAllBytesAsync(path);
            Assert.Equal(content.Length, written.Length);
            Assert.True(content.AsSpan().SequenceEqual(written), "分段下载的文件内容与源不一致");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 大文件_进度回调最终达到100百分比()
    {
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 50_000);
        await using var server = await TestServer.Start();
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            DownloadProgress? last = null;
            var done = await downloader.TryDownloadAsync(server.Url, path, p => last = p);
            Assert.True(done);
            Assert.NotNull(last);
            Assert.Equal(1.0, last!.Fraction);
            Assert.Equal(content.Length, last!.BytesTotal);
            Assert.Equal(content.Length, last!.BytesDone);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 服务器忽略Range_返回完整内容时判定不支持并回退()
    {
        // 服务器对 Range 请求返回 200 + 完整内容（很多 CDN 的行为）
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 1000);
        await using var server = await TestServer.Start(ignoreRange: true);
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var done = await downloader.TryDownloadAsync(server.Url, path);
            Assert.False(done);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 取消时抛出取消异常()
    {
        await using var server = await TestServer.Start(responseDelay: TimeSpan.FromSeconds(5));
        server.Content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 1000);

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => downloader.TryDownloadAsync(server.Url, path, null, null, cts.Token));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 服务器返回的ContentRange与请求不一致时判定不支持并回退()
    {
        // 服务器声称的区间和实际请求的不一致 —— 继续写下去会得到错乱的文件，
        // 必须当成"不支持 Range"处理，让调用方回落到顺序下载。
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 4096);
        await using var server = await TestServer.Start(wrongContentRange: true);
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var done = await downloader.TryDownloadAsync(server.Url, path);
            Assert.False(done);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 服务器总是截断响应时最终失败而不是无限重试()
    {
        // 服务器声称的长度总比实际写出的多 —— 每一段都读不满。
        // 正确行为：重试到上限后**如实失败**，而不是无限重试把界面卡死。
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 20_000);
        await using var server = await TestServer.Start(alwaysTruncate: true);
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var ex = await Assert.ThrowsAnyAsync<IOException>(
                () => downloader.TryDownloadAsync(server.Url, path));
            // 必须是"某一段提前结束"这个具体原因，而不是笼统的"总量对不上"
            // —— 否则把"段提前结束却不报错"的缺陷注入进去也测不出来。
            Assert.Contains("提前结束", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 探测请求只发一次()
    {
        // 引擎必须先发一次 bytes=0-0 探测服务器是否支持 Range，
        // 然后才开始真正的分段下载 —— 探测不能反复发（否则每个连接都白跑一趟）。
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 50_000);
        await using var server = await TestServer.Start();
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var ok = await downloader.TryDownloadAsync(server.Url, path);
            Assert.True(ok);
            Assert.Equal(1, server.ProbeRequestCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task 小文件不启用分段下载()
    {
        // 小于阈值的文件**不启用分段下载**：探测一次拿到总长度后发现太小，
        // 直接返回 false，交给调用方走顺序下载（PCL 原版同样是这个语义）。
        // 关键：返回 false 时**不能留下半个文件**，也不能再发第二次请求。
        var content = RandomBytes(1024);
        await using var server = await TestServer.Start();
        server.Content = content;

        using var http = new HttpClient();
        var downloader = new RangeDownloader(http);
        var path = TempPath();
        try
        {
            var ok = await downloader.TryDownloadAsync(server.Url, path);
            Assert.False(ok);
            // 探测一次（必须先知道大小才能判断小不小），仅此一次
            Assert.Equal(1, server.ProbeRequestCount);
            Assert.Equal(1, server.RequestCount);
            // 不启用分段就不能留下半成品
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>
    /// 手写的最小 HTTP 服务器（只用 TcpListener），用于精确控制 Range 行为。
    /// </summary>
    private sealed class TestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        // 计数：用来断言"引擎只发了一次探测请求"这类行为
        private int _requestCount;
        private int _probeRequestCount;

        /// <summary>收到的 HTTP 请求总数。</summary>
        public int RequestCount => Volatile.Read(ref _requestCount);

        /// <summary>其中属于 Range 探测（bytes=0-0）的请求数。</summary>
        public int ProbeRequestCount => Volatile.Read(ref _probeRequestCount);

        public byte[] Content { get; set; } = Array.Empty<byte>();
        public bool SupportRange { get; init; } = true;
        public bool IgnoreRange { get; init; }
        public bool WrongContentRange { get; init; }
        public bool AlwaysTruncate { get; init; }
        public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

        public string Url { get; }

        private TestServer(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/file.bin";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public static Task<TestServer> Start(
            bool supportRange = true,
            bool ignoreRange = false,
            bool wrongContentRange = false,
            bool alwaysTruncate = false,
            TimeSpan responseDelay = default)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new TestServer(listener)
            {
                SupportRange = supportRange,
                IgnoreRange = ignoreRange,
                WrongContentRange = wrongContentRange,
                AlwaysTruncate = alwaysTruncate,
                ResponseDelay = responseDelay
            };
            return Task.FromResult(server);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer);
                    var headerText = Encoding.ASCII.GetString(buffer, 0, read);

                    if (ResponseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(ResponseDelay, _cts.Token);
                        if (_cts.IsCancellationRequested) return;
                    }

                    await WriteResponseAsync(stream, headerText);
                }
            }
            catch (Exception)
            {
                // 测试服务器：连接被客户端取消时忽略
            }
        }

        private async Task WriteResponseAsync(NetworkStream stream, string rawHeader)
        {
            Interlocked.Increment(ref _requestCount);
            var rangeHeader = ParseRangeHeader(rawHeader);
            if (rangeHeader is not null && rangeHeader.EndsWith("0-0", StringComparison.Ordinal))
                Interlocked.Increment(ref _probeRequestCount);
            long from = 0, to = Content.Length - 1;
            var isRange = rangeHeader is not null;

            if (isRange)
            {
                var spec = rangeHeader!["bytes=".Length..];
                var dash = spec.IndexOf('-');
                var fromText = spec[..dash];
                var toText = spec[(dash + 1)..];
                if (long.TryParse(fromText, out var f)) from = f;
                if (long.TryParse(toText, out var t)) to = t;
            }

            if (isRange && SupportRange && !IgnoreRange)
            {
                if (to >= Content.Length) to = Content.Length - 1;
                var length = to - from + 1;
                // AlwaysTruncate：只发一半的数据，但 Content-Length 也照实写成一半
                // （否则 HttpClient 自己就会因"响应提前结束"抛错，测不到我们自己的检查）。
                if (AlwaysTruncate)
                {
                    var half = Math.Max(0, (int)length / 2);
                    var headTrunc = "HTTP/1.1 206 Partial Content\r\n" +
                                    $"Content-Length: {half}\r\n" +
                                    $"Content-Range: bytes {from}-{to}/{Content.Length}\r\n" +
                                    "Accept-Ranges: bytes\r\n" +
                                    "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headTrunc));
                    await stream.WriteAsync(Content.AsMemory((int)from, half));
                    await stream.FlushAsync();
                    return;
                }
                // WrongContentRange：服务器声称返回的区间与实际请求的不一致
                // （现实中会出现于缓存错乱的 CDN / 代理改写响应头）。
                // 注意：探测请求（bytes=0-0）必须**如实**回答，否则探测阶段
                // 就退化了，测不到"分段阶段的 Content-Range 校验"。
                var isProbe = from == 0 && to == 0;
                var reportedFrom = WrongContentRange && !isProbe ? from + 1 : from;
                var head = "HTTP/1.1 206 Partial Content\r\n" +
                           $"Content-Length: {length}\r\n" +
                           $"Content-Range: bytes {reportedFrom}-{to}/{Content.Length}\r\n" +
                           "Accept-Ranges: bytes\r\n" +
                           "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(Content.AsMemory((int)from, (int)length));
            }
            else
            {
                var head = "HTTP/1.1 200 OK\r\n" +
                           $"Content-Length: {Content.Length}\r\n" +
                           "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(Content);
            }
            await stream.FlushAsync();
        }

        private static string? ParseRangeHeader(string rawHeader)
        {
            foreach (var headerLine in rawHeader.Split("\r\n"))
            {
                var colon = headerLine.IndexOf(':');
                if (colon <= 0) continue;
                var name = headerLine[..colon].Trim();
                if (!name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                var value = headerLine[(colon + 1)..].Trim();
                if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return value;
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
