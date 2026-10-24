using System.Net;
using System.Net.Http;
using System.Text;
using UMango.Core.Network.Http;
using UMango.Core.Utils;

namespace UMango.Core.Tests.Network;

/// <summary>
/// HTTP 扩展层的测试。
///
/// 这些都是纯函数/纯内存行为，不发真实网络请求 ——
/// 真网络行为由 UpdateChecker 等上层测试用假 HttpService 覆盖。
/// </summary>
public class HttpExtensionTests
{
    // ── 请求头 ────────────────────────────────────────────

    [Fact]
    public void 普通请求头加在请求上()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithHeader("X-Test", "1");
        Assert.True(request.Headers.TryGetValues("X-Test", out var values));
        Assert.Equal(["1"], values);
    }

    [Fact]
    public void 内容头加在内容上()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        // 用一个与默认不同的类型，这样"头加错了地方"会立刻显形：
        // 加错时内容头仍是 StringContent 的默认 text/plain。
        request.WithContent("body").WithHeader("Content-Type", "application/json");
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void 内容头不会被误加到请求上()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithContent("body").WithHeader("Content-Type", "application/json");
        // Content-Type 放在 request.Headers 上会在真正发送时抛
        // "Misused header name" —— .NET 在**访问**该名字时就会抛。
        Assert.Throws<InvalidOperationException>(() => request.Headers.Contains("Content-Type"));
    }

    [Fact]
    public void 多个请求头可以一次加上()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithHeaders(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
        Assert.True(request.Headers.Contains("A"));
        Assert.True(request.Headers.Contains("B"));
    }

    [Fact]
    public void 认证头使用标准格式()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithBearerToken("abc");
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("abc", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void 空令牌会被拒绝()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        Assert.Throws<ArgumentException>(() => request.WithBearerToken(""));
    }

    // ── 内容 ──────────────────────────────────────────────

    [Fact]
    public async Task 字符串内容按utf8发送()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithContent("你好");
        Assert.Equal("你好", await request.Content!.ReadAsStringAsync());
        Assert.Equal("utf-8", request.Content.Headers.ContentType!.CharSet);
    }

    [Fact]
    public async Task json内容用宽松配置序列化()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithJsonContent(new Sample { Name = "umango", Count = 3 });
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\"", body);
        Assert.Contains("\"count\"", body);
        Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task 表单内容用键值对构造()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithFormContent(new[] { new KeyValuePair<string, string>("a", "1"), new("b", "2") });
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("a=1", body);
        Assert.Contains("b=2", body);
    }

    // ── Cookie ────────────────────────────────────────────

    [Fact]
    public void cookie被加到请求头()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithCookie("a", "1");
        Assert.Equal("a=1", request.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public void 多个cookie会被合并()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithCookie("a", "1").WithCookie("b", "2");
        var cookie = request.Headers.GetValues("Cookie").Single();
        Assert.Contains("a=1", cookie);
        Assert.Contains("b=2", cookie);
    }

    [Fact]
    public void cookie值里的分号会被转义()
    {
        // 回归：不转义的话，一个带分号的值就能伪造出额外的 Cookie。
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithCookie("a", "x;y");
        var cookie = request.Headers.GetValues("Cookie").Single();
        Assert.Equal("a=x%3By", cookie);
    }

    [Fact]
    public void cookie值里的空格会被转义()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithCookie("a", "x y");
        Assert.Equal("a=x%20y", request.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public void 普通cookie值不被转义()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithCookie("a", "xyz-123_456");
        Assert.Equal("a=xyz-123_456", request.Headers.GetValues("Cookie").Single());
    }

    // ── 响应 ──────────────────────────────────────────────

    [Fact]
    public async Task 响应可以读成字符串()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") };
        Assert.Equal("hello", await response.AsStringAsync());
        Assert.True(response.IsSuccess());
    }

    [Fact]
    public async Task 响应可以读成字节数组()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        Assert.Equal([1, 2, 3], await response.AsByteArrayAsync());
    }

    [Fact]
    public async Task 响应可以读成json()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"umango\",\"count\":3}")
        };
        var sample = await response.AsJsonAsync<Sample>();
        Assert.Equal("umango", sample!.Name);
        Assert.Equal(3, sample.Count);
    }

    [Fact]
    public async Task 非法json会被翻译成数据错误()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>err</html>") };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => response.AsJsonAsync<Sample>());
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task 响应头与内容头可以合并读取()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        response.Headers.TryAddWithoutValidation("X-A", "1");
        response.Content.Headers.TryAddWithoutValidation("X-B", "2");
        var headers = response.GetHeaders();
        Assert.Contains("X-A", headers.Keys);
        Assert.Contains("X-B", headers.Keys);
    }

    [Fact]
    public void 没有内容时读内容头不会崩()
    {
        // 204/304 响应下 Content 可能为 null
        using var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        Assert.Empty(response.GetContentHeaders());
    }

    [Fact]
    public void 取单个头值()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-A", "1");
        Assert.Equal("1", response.GetFirstHeaderValue("X-A"));
        Assert.Null(response.GetFirstHeaderValue("X-Missing"));
    }

    [Fact]
    public void 非成功状态会抛异常()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        var ex = Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task 带内容的状态异常会带上响应体()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream down") };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => response.EnsureSuccessStatusCodeWithContentAsync());
        Assert.Contains("upstream down", ex.Message);
    }

    // ── 克隆 ──────────────────────────────────────────────

    [Fact]
    public async Task 克隆会带上请求头()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithHeader("X-A", "1");
        using var clone = await request.CloneAsync();
        Assert.Equal("1", clone.Headers.GetValues("X-A").Single());
        Assert.Equal(request.Method, clone.Method);
    }

    [Fact]
    public async Task 克隆会带上内容()
    {
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithContent("payload");
        using var clone = await request.CloneAsync();
        Assert.Equal("payload", await clone.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task 克隆后的内容可以独立读取()
    {
        // 回归：HttpContent 只能读一次，克隆必须真的复制出新的流。
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithContent("payload");
        using var clone = await request.CloneAsync();
        Assert.Equal("payload", await clone.Content!.ReadAsStringAsync());
        Assert.Equal("payload", await clone.Content!.ReadAsStringAsync());
    }

    // ── 退避与重试判定 ────────────────────────────────────

    [Fact]
    public void 退避时长按指数增长并封顶()
    {
        Assert.Equal(1000, HttpSender.DefaultDelayProvider(1).TotalMilliseconds);
        Assert.Equal(2000, HttpSender.DefaultDelayProvider(2).TotalMilliseconds);
        Assert.Equal(4000, HttpSender.DefaultDelayProvider(3).TotalMilliseconds);
        Assert.Equal(30000, HttpSender.DefaultDelayProvider(20).TotalMilliseconds);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(429, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    public void 只有服务端错误才值得重试(int status, bool expected)
        => Assert.Equal(expected, HttpSender.ShouldRetryStatus(status));

    // ── 代理 ──────────────────────────────────────────────

    [Fact]
    public void 代理字符串可以解析出多条()
    {
        var items = HttpProxyManager.ParseProxyString("http=127.0.0.1:8080;socks=127.0.0.1:1080");
        Assert.Equal(2, items.Count);
        Assert.Equal(ProxyProtocol.Http, items[0].Protocol);
        Assert.Equal(ProxyProtocol.Socks, items[1].Protocol);
    }

    [Fact]
    public void 单条代理可以解析()
    {
        var items = HttpProxyManager.ParseProxyString("http://127.0.0.1:10808");
        Assert.Single(items);
        Assert.Contains("127.0.0.1", items[0].Address);
    }

    [Fact]
    public void 裸地址会补上协议()
    {
        var items = HttpProxyManager.ParseProxyString("127.0.0.1:10808");
        Assert.Single(items);
        Assert.StartsWith("http://", items[0].Address);
    }

    [Fact]
    public void 空代理字符串解析出空列表()
        => Assert.Empty(HttpProxyManager.ParseProxyString("   "));

    [Fact]
    public void 非法段会被忽略()
    {
        var items = HttpProxyManager.ParseProxyString("http=127.0.0.1:8080;garbage;=x;y=1");
        Assert.Single(items);
    }

    [Fact]
    public void 不使用代理时直接返回目标地址()
    {
        var manager = new HttpProxyManager { Mode = ProxyMode.NoProxy };
        var target = new Uri("https://example.com/x");
        Assert.Equal(target, manager.GetProxy(target));
    }

    [Fact]
    public void 不使用代理时本机地址算被绕过()
    {
        var manager = new HttpProxyManager { Mode = ProxyMode.NoProxy };
        Assert.True(manager.IsBypassed(new Uri("https://localhost/x")));
    }

    // ── 缓存 ──────────────────────────────────────────────

    [Fact]
    public void 缓存时长取自cache_control()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30) };
        Assert.Equal(TimeSpan.FromSeconds(30), HttpCacheHandler.ComputeTtl(response));
    }

    [Fact]
    public void 没有cache_control时用默认时长()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.Equal(TimeSpan.FromMinutes(5), HttpCacheHandler.ComputeTtl(response));
    }

    [Fact]
    public void 缓存元数据可以往返()
    {
        var meta = new HttpCacheMetadata { ETag = "\"abc\"", LastModified = "2024-01-01T00:00:00Z", StatusCode = 200 };
        var bytes = HttpCacheHandler.SerializeMetadata(meta);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"eTag\"", text);
        Assert.Contains("abc", text);
    }

    // ── 宽松 JSON ─────────────────────────────────────────

    [Fact]
    public void 宽松json接受小写属性名()
    {
        var node = JsonCompat.ParseNode("{\"Name\":\"x\"}");
        Assert.Equal("x", node!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void 宽松json接受尾逗号与注释()
    {
        var node = JsonCompat.ParseNode("{/* c */ \"a\": 1, }");
        Assert.Equal(1, node!["a"]!.GetValue<int>());
    }

    [Fact]
    public void 宽松json把数字字符串读成数字()
    {
        var sample = System.Text.Json.JsonSerializer.Deserialize<Sample>(
            "{\"count\":\"7\"}", JsonCompat.SerializerOptions);
        Assert.Equal(7, sample!.Count);
    }

    [Fact]
    public void 宽松json把字符串布尔读成布尔()
    {
        var sample = System.Text.Json.JsonSerializer.Deserialize<Sample>(
            "{\"flag\":\"true\"}", JsonCompat.SerializerOptions);
        Assert.True(sample!.Flag);
    }

    [Fact]
    public void 宽松json把unix时间戳读成日期()
    {
        var sample = System.Text.Json.JsonSerializer.Deserialize<Sample>(
            "{\"when\":1700000000}", JsonCompat.SerializerOptions);
        Assert.Equal(2023, sample!.When.Year);
    }

    [Fact]
    public void 宽松json把毫秒时间戳读成日期()
    {
        var sample = System.Text.Json.JsonSerializer.Deserialize<Sample>(
            "{\"when\":1700000000000}", JsonCompat.SerializerOptions);
        Assert.Equal(2023, sample!.When.Year);
    }

    [Fact]
    public void 宽松json把布尔写成字符串也能读回()
    {
        var sample = System.Text.Json.JsonSerializer.Deserialize<Sample>(
            "{\"flag\":true}", JsonCompat.SerializerOptions);
        Assert.True(sample!.Flag);
    }

    // ── 重试发送 ──────────────────────────────────────────

    [Fact]
    public async Task 服务端错误会触发重试()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");

        using var response = await request.SendAsync(client, retryTimes: 2,
            delayProvider: _ => TimeSpan.Zero);

        Assert.Equal(3, handler.Calls);       // 首次 + 2 次重试
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task 客户端错误不会重试()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");

        using var response = await request.SendAsync(client, retryTimes: 3,
            delayProvider: _ => TimeSpan.Zero);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 成功时不重试()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");

        using var response = await request.SendAsync(client, retryTimes: 3,
            delayProvider: _ => TimeSpan.Zero);

        Assert.Equal(1, handler.Calls);
        Assert.True(response.IsSuccess());
    }

    [Fact]
    public async Task 传输失败会重试并最终抛出()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");

        await Assert.ThrowsAsync<HttpRequestException>(() => request.SendAsync(client,
            retryTimes: 2, delayProvider: _ => TimeSpan.Zero));

        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task 主动取消不会重试()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.SendAsync(client,
            retryTimes: 3, delayProvider: _ => TimeSpan.Zero, ct: cts.Token));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task 重试时每次都用新的请求副本()
    {
        // 回归：HttpRequestMessage 不可重用。若不克隆，第二次尝试会抛
        // "The request message was already sent"。
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithHeader("X-A", "1");

        using var response = await request.SendAsync(client, retryTimes: 2,
            delayProvider: _ => TimeSpan.Zero);

        Assert.Equal(3, handler.Calls);
        Assert.Equal(["1"], handler.LastRequest!.Headers.GetValues("X-A").ToArray());
    }

    [Fact]
    public async Task 重试回调会被调用()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");
        var retries = new List<HttpRetryInfo>();

        using var response = await request.SendAsync(client, retryTimes: 2,
            onRetry: retries.Add, delayProvider: _ => TimeSpan.FromMilliseconds(5));

        Assert.Equal(2, retries.Count);
        Assert.Equal(1, retries[0].Attempt);
        Assert.Equal(3, retries[0].MaxAttempts);
    }

    [Fact]
    public async Task 重试时请求体不会丢()
    {
        var handler = new FakeHandler(async r =>
        {
            if (r.Content is not null) await r.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = new HttpClient(handler);
        using var request = HttpRequestFactory.Create("https://example.com");
        request.WithContent("payload");

        using var response = await request.SendAsync(client, retryTimes: 2,
            delayProvider: _ => TimeSpan.Zero);

        Assert.Equal(3, handler.Calls);
        Assert.Equal("payload", handler.LastBody);
    }

    /// <summary>假处理器：按委托返回响应或抛出异常。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return await _responder(request);
        }
    }

    private sealed class Sample
    {
        public string? Name { get; set; }
        public int Count { get; set; }
        public bool Flag { get; set; }
        public DateTime When { get; set; }
    }
}
