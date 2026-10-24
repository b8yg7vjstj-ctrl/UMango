using System.Net;
using System.Text;
using UMango.Core.Download;
using UMango.Core.Download.Engines;
using Xunit;

namespace UMango.Core.Tests.Download;

/// <summary>
/// 下载文件模型与内置下载引擎的行为测试。
///
/// 这组测试盯的是两个真实缺陷：
///   ① 引擎直接写目标路径 → 下载失败会把原来完好的文件毁掉；
///   ② 没有"已存在即跳过"的语义（PCL 的 <c>DownloadFile</c>）。
/// </summary>
public class DownloadFileTests
{
    [Fact]
    public void 目标不存在时不是完整的()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        var file = new DownloadFile("https://x/a.bin", path);

        Assert.False(file.Exists);
        Assert.False(file.IsComplete());
    }

    [Fact]
    public void 存在但为空的文件不算完整()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);

        try
        {
            var file = new DownloadFile("https://x/a.bin", path);
            Assert.True(file.Exists);
            // 0 字节不能当成"已完成"，否则会永远跳过真正需要下载的文件
            Assert.False(file.IsComplete());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 存在且非空时算完整()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hello");

        try
        {
            Assert.True(new DownloadFile("https://x/a.bin", path).IsComplete());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 有期望校验值且匹配时算完整()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = Encoding.UTF8.GetBytes("hello world");
        File.WriteAllBytes(path, content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

        try
        {
            Assert.True(new DownloadFile("https://x/a.bin", path, hash).IsComplete());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 有期望校验值但不匹配时不算完整()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hello world");

        try
        {
            Assert.False(new DownloadFile("https://x/a.bin", path, new string('0', 64)).IsComplete());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 校验值大小写不敏感()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = Encoding.UTF8.GetBytes("abc");
        File.WriteAllBytes(path, content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));

        try
        {
            Assert.True(new DownloadFile("https://x/a.bin", path, hash.ToLowerInvariant()).IsComplete());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>内置下载引擎的落位与跳过行为。</summary>
public class BuiltinHttpEngineTests
{
    [Fact]
    public async Task 下载成功后文件内容正确()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var payload = Encoding.UTF8.GetBytes("hello umango");

        try
        {
            var engine = new BuiltinHttpEngine();
            // 引擎用全局代理建 handler，这里改用本地 stub server
            var result = await DownloadViaStubAsync(engine, dir, payload);

            Assert.True(result.Success, result.Message);
            var written = File.ReadAllBytes(Path.Combine(dir, "a.bin"));
            Assert.Equal(payload, written);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 失败时不会留下半个文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "a.bin");
        File.WriteAllText(target, "原有内容");

        try
        {
            var engine = new BuiltinHttpEngine();
            // 404 → 失败
            var result = await DownloadViaStubAsync(engine, dir, Array.Empty<byte>(), HttpStatusCode.NotFound);

            Assert.False(result.Success);
            // 关键：原来那个完好的文件不能被毁掉
            Assert.Equal("原有内容", File.ReadAllText(target));
            // 也不该留下 .part 残留
            Assert.False(File.Exists(target + ".part"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 已有完整文件且要求跳过时不会重新下载()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "a.bin");
        File.WriteAllText(target, "原有内容");

        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await DownloadViaStubAsync(engine, dir, Encoding.UTF8.GetBytes("新内容"),
                HttpStatusCode.OK, skipExisting: true);

            Assert.True(result.Success);
            Assert.Equal("skipped", result.ErrorCode);
            Assert.Equal("原有内容", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 已有文件但要求跳过时不满足条件则重新下载()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), []);

        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await DownloadViaStubAsync(engine, dir, Encoding.UTF8.GetBytes("新内容"),
                HttpStatusCode.OK, skipExisting: true);

            Assert.True(result.Success);
            Assert.Null(result.ErrorCode);
            Assert.Equal("新内容", File.ReadAllText(Path.Combine(dir, "a.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task 校验不通过时报错且不覆盖原文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "a.bin");
        File.WriteAllText(target, "原有内容");

        try
        {
            var engine = new BuiltinHttpEngine();
            var result = await DownloadViaStubAsync(engine, dir, Encoding.UTF8.GetBytes("新内容"),
                HttpStatusCode.OK, expectedSha256: new string('0', 64));

            Assert.False(result.Success);
            Assert.Equal("checksum_mismatch", result.ErrorCode);
            Assert.Equal("原有内容", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>起一个本地 HttpListener 当服务器，返回固定内容。</summary>
    private static async Task<DownloadResult> DownloadViaStubAsync(
        BuiltinHttpEngine engine, string dir, byte[] payload,
        HttpStatusCode status = HttpStatusCode.OK, bool skipExisting = false,
        string? expectedSha256 = null)
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serve = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = (int)status;
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.Close();
            }
            catch
            {
                // 监听器已停止
            }
        });

        try
        {
            var request = new DownloadRequest(
                Url: prefix + "a.bin",
                OutputDirectory: dir,
                OutputFileName: "a.bin",
                ExpectedSha256: expectedSha256,
                SkipExisting: skipExisting);

            return await engine.DownloadAsync(request, null, null, CancellationToken.None);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
