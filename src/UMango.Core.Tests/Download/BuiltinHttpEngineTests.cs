using System.Net;
using System.Net.Sockets;
using System.Text;
using UMango.Core.Download;
using UMango.Core.Download.Engines;
using Xunit;

namespace UMango.Core.Tests.Download;

/// <summary>
/// 内置下载引擎的端到端测试（真实本地 HTTP 服务器）。
///
/// 盯的是**分段下载接入引擎后**的行为：
///   · 大文件走分段后内容必须与源完全一致（偏移写错就会错位）；
///   · 服务器不支持 Range 时必须回落到顺序下载，而不是直接失败；
///   · 目标已存在且正确时仍然跳过（PCL 的 DownloadFile 语义）；
///   · 下载失败不能把原来完好的目标文件毁掉。
/// </summary>
public sealed class RangeEngineIntegrationTests
{
    [Fact]
    public async Task 大文件_经分段下载后内容与源一致()
    {
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 123_456);
        await using var server = await LocalHttpServer.Start();
        server.Content = content;

        var dir = NewDir();
        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest(server.Url, dir, "big.bin"), null, null);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.OutputPath);
            var written = await File.ReadAllBytesAsync(result.OutputPath!);
            Assert.True(content.AsSpan().SequenceEqual(written), "分段下载落盘的内容与源不一致");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 小文件_顺序下载也成功()
    {
        var content = RandomBytes(2048);
        await using var server = await LocalHttpServer.Start();
        server.Content = content;

        var dir = NewDir();
        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest(server.Url, dir, "small.bin"), null, null);

            Assert.True(result.Success, result.Message);
            var written = await File.ReadAllBytesAsync(result.OutputPath!);
            Assert.True(content.AsSpan().SequenceEqual(written));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 服务器不支持Range时回落到顺序下载并成功()
    {
        var content = RandomBytes((int)RangeDownloader.SmallFileThreshold + 4096);
        await using var server = await LocalHttpServer.Start(supportRange: false);
        server.Content = content;

        var dir = NewDir();
        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest(server.Url, dir, "norange.bin"), null, null);

            Assert.True(result.Success, result.Message);
            var written = await File.ReadAllBytesAsync(result.OutputPath!);
            Assert.True(content.AsSpan().SequenceEqual(written));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 目标已存在且正确时跳过下载()
    {
        await using var server = await LocalHttpServer.Start();
        server.Content = RandomBytes(1024);

        var dir = NewDir();
        try
        {
            var target = Path.Combine(dir, "skip.bin");
            await File.WriteAllBytesAsync(target, server.Content);

            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest(server.Url, dir, "skip.bin") { SkipExisting = true }, null, null);

            Assert.True(result.Success);
            Assert.Equal("skipped", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 下载失败不会毁掉已有的目标文件()
    {
        await using var server = await LocalHttpServer.Start();
        server.Content = RandomBytes(1024);

        var dir = NewDir();
        try
        {
            var target = Path.Combine(dir, "keep.bin");
            await File.WriteAllTextAsync(target, "原有内容");

            // 指向一个不存在的地址 -> 失败
            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest("http://127.0.0.1:1/nope.bin", dir, "keep.bin"), null, null);

            Assert.False(result.Success);
            Assert.Equal("原有内容", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 校验失败时不落位()
    {
        var content = RandomBytes(1024);
        await using var server = await LocalHttpServer.Start();
        server.Content = content;

        var dir = NewDir();
        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await engine.DownloadAsync(
                new DownloadRequest(server.Url, dir, "bad.bin") { ExpectedSha256 = new string('0', 64) },
                null, null);

            Assert.False(result.Success);
            Assert.Equal("checksum_mismatch", result.ErrorCode);
            Assert.False(File.Exists(Path.Combine(dir, "bad.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>最小本地 HTTP 服务器（支持 / 忽略 Range 两种模式）。</summary>
    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public byte[] Content { get; set; } = Array.Empty<byte>();
        public bool SupportRange { get; init; } = true;
        public string Url { get; }

        private LocalHttpServer(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/file.bin";
            _loop = Task.Run(AcceptLoopAsync);
        }

        public static Task<LocalHttpServer> Start(bool supportRange = true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LocalHttpServer(listener) { SupportRange = supportRange });
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
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
                    var raw = Encoding.ASCII.GetString(buffer, 0, read);
                    var range = ParseRange(raw);

                    long from = 0, to = Content.Length - 1;
                    if (range is not null)
                    {
                        var spec = range["bytes=".Length..];
                        var dash = spec.IndexOf('-');
                        if (long.TryParse(spec[..dash], out var f)) from = f;
                        if (long.TryParse(spec[(dash + 1)..], out var t)) to = t;
                    }

                    if (range is not null && SupportRange)
                    {
                        if (to >= Content.Length) to = Content.Length - 1;
                        var length = to - from + 1;
                        var head = "HTTP/1.1 206 Partial Content\r\n" +
                                   $"Content-Length: {length}\r\n" +
                                   $"Content-Range: bytes {from}-{to}/{Content.Length}\r\n" +
                                   "Accept-Ranges: bytes\r\nConnection: close\r\n\r\n";
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
            }
            catch (Exception)
            {
                // 客户端取消时忽略
            }
        }

        private static string? ParseRange(string raw)
        {
            foreach (var line in raw.Split("\r\n"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (!line[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(colon + 1)..].Trim();
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
