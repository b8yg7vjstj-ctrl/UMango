using System.Net;
using System.Text;
using UMango.Core.Network.Http.Server;
using Xunit;

namespace UMango.Core.Tests.Network.Http.Server;

/// <summary>
/// 本地 HTTP 服务端的行为。
///
/// 这一组基本是在跟 PCL-CE 原版的**六个真实缺陷**对照：
/// 每一个用例都对应原版里一处会真的坏掉的地方。
/// </summary>
public class HttpServerTests
{
    /// <summary>一个测试用的小服务端。</summary>
    private sealed class TestServer : HttpServer
    {
        public TestServer(ushort port = 0) : base([IPAddress.Loopback], port)
        {
        }

        public int InitCount { get; private set; }

        protected override void Init()
        {
            InitCount++;
            Register("GET", "/ping", _ => Task.FromResult(HttpRouteResponse.Text("pong")));
            Register("GET", "/json", _ => Task.FromResult(HttpRouteResponse.Json(new { ok = true })));
            Register("GET", "/boom", _ => throw new InvalidOperationException("炸了"));
            Register("GET", "/redirect", _ => Task.FromResult(HttpRouteResponse.Redirect("/ping")));
            Register("GET", "*", _ => Task.FromResult(HttpRouteResponse.Text("fallback")));
        }
    }

    private static string BaseUrl(HttpServer server) => $"http://127.0.0.1:{server.Port}";

    [Fact]
    public async Task 路由能正常响应()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new HttpClient();
        var body = await client.GetStringAsync(BaseUrl(server) + "/ping");

        Assert.Equal("pong", body);
    }

    [Fact]
    public async Task 未知路径落到通配路由()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new HttpClient();
        var body = await client.GetStringAsync(BaseUrl(server) + "/whatever");

        Assert.Equal("fallback", body);
    }

    [Fact]
    public async Task 没有通配路由时未知路径返回404()
    {
        using var server = new NoWildcardServer();
        server.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync(BaseUrl(server) + "/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 处理器抛异常时返回500()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync(BaseUrl(server) + "/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task JSON_响应带正确的内容类型()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync(BaseUrl(server) + "/json");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", body);
    }

    [Fact]
    public async Task 重定向会返回302并带上目标()
    {
        using var server = new TestServer();
        server.Start();

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await client.GetAsync(BaseUrl(server) + "/redirect");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ping", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// 缺陷①：原版把 IPv6 回环直接拼进前缀（http://::1:8080/），
    /// 那不是合法 URI —— 也就是说原版**默认监听的地址根本起不来**。
    /// 修法：IPv6 用方括号包裹。
    /// </summary>
    [Fact]
    public void IPv6前缀带方括号()
    {
        Assert.Equal("http://[::1]:8080/", HttpServer.FormatPrefix(IPAddress.IPv6Loopback, 8080));
        Assert.Equal("http://127.0.0.1:8080/", HttpServer.FormatPrefix(IPAddress.Loopback, 8080));
    }

    /// <summary>
    /// 缺陷②：即使前缀写对了，某些平台（实测 macOS）也绑不上 IPv6 回环。
    /// 这时不能整个服务器起不来 —— 至少要给出**看得见**的原因。
    /// </summary>
    [Fact]
    public void 只监听IPv6时要么能起来要么给出明确原因()
    {
        using var server = new IPv6Server();

        try
        {
            server.Start();
        }
        catch (InvalidOperationException ex)
        {
            // 绑不上就必须说清楚是哪个地址、什么原因
            Assert.Contains("::1", ex.Message);
            return;
        }

        Assert.True(server.IsRunning);
        Assert.Equal(1, server.InitCount);
    }

    /// <summary>缺陷③：端口是"先占后放"选出来的，中间会被抢占。</summary>
    [Fact]
    public void 端口为0时能自动分配到可用端口()
    {
        using var server = new TestServer();
        server.Start();

        Assert.NotEqual(0, server.Port);
        Assert.True(server.IsRunning);
    }

    /// <summary>缺陷③（续）：指定的端口起不来时，错误要能被看到，不能静默。</summary>
    [Fact]
    public void 指定端口被占用时抛出明确异常()
    {
        // 先占住一个端口
        var occupied = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = (ushort)((IPEndPoint)occupied.LocalEndpoint).Port;

        try
        {
            using var server = new TestServer(port);
            var ex = Assert.Throws<InvalidOperationException>(() => server.Start());
            Assert.Contains("端口", ex.Message);
        }
        finally
        {
            occupied.Stop();
        }
    }

    /// <summary>缺陷④：重复 Start 会炸；且路由初始化只该发生一次。</summary>
    [Fact]
    public void 重复启动会抛异常且路由只初始化一次()
    {
        using var server = new TestServer();
        server.Start();

        Assert.Throws<InvalidOperationException>(() => server.Start());
        Assert.Equal(1, server.InitCount);
    }

    /// <summary>
    /// 缺陷⑤：原版 Stop 之后无法重启 ——
    /// 令牌被取消且不重建，重启后循环立刻退出，服务器"在跑"但一个请求都处理不了。
    /// </summary>
    [Fact]
    public async Task 停止之后还能重新启动并继续服务()
    {
        using var server = new TestServer();
        server.Start();
        using var client = new HttpClient();

        Assert.Equal("pong", await client.GetStringAsync(BaseUrl(server) + "/ping"));

        server.Stop();
        Assert.False(server.IsRunning);

        server.Start();
        Assert.True(server.IsRunning);
        Assert.Equal("pong", await client.GetStringAsync(BaseUrl(server) + "/ping"));

        // 路由初始化不应该因为重启而重跑
        Assert.Equal(1, server.InitCount);
    }

    /// <summary>
    /// 缺陷②：原版把请求处理丢进 Task.Run(..., cancellationToken)，
    /// 停止时正在处理的请求被静默丢弃 —— 客户端那边就一直等到超时。
    /// </summary>
    [Fact]
    public async Task 停止时正在处理的请求不会挂死客户端()
    {
        using var server = new SlowServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var pending = client.GetAsync(BaseUrl(server) + "/slow");

        // 让请求进到处理器里
        await Task.Delay(200);
        server.Stop();

        // 处理器最终会完成（哪怕响应写不出去），客户端不该一直挂着
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(pending, completed);

        // 响应写不出去时客户端会收到异常 —— 那也比一直挂着好
        try { await pending; } catch { /* 预期内 */ }
    }

    [Fact]
    public void 释放后再次释放不会抛异常()
    {
        var server = new TestServer();
        server.Start();

        server.Dispose();
        server.Dispose();
    }

    [Fact]
    public void 释放之后不能再启动()
    {
        var server = new TestServer();
        server.Dispose();

        Assert.Throws<ObjectDisposedException>(() => server.Start());
    }

    [Fact]
    public async Task 响应文本支持非ASCII()
    {
        using var server = new UnicodeServer();
        server.Start();

        using var client = new HttpClient();
        var body = await client.GetStringAsync(BaseUrl(server) + "/cn");

        Assert.Equal("公告", body);
    }

    private sealed class NoWildcardServer : HttpServer
    {
        public NoWildcardServer() : base([IPAddress.Loopback])
        {
        }

        protected override void Init() => Register("GET", "/only", _ => Task.FromResult(HttpRouteResponse.Text("ok")));
    }

    private sealed class IPv6Server : HttpServer
    {
        public IPv6Server() : base([IPAddress.IPv6Loopback])
        {
        }

        public int InitCount { get; private set; }

        protected override void Init()
        {
            InitCount++;
            Register("GET", "/ping", _ => Task.FromResult(HttpRouteResponse.Text("pong")));
        }
    }

    private sealed class SlowServer : HttpServer
    {
        public SlowServer() : base([IPAddress.Loopback])
        {
        }

        protected override void Init() => Register("GET", "/slow", async _ =>
        {
            await Task.Delay(300);
            return HttpRouteResponse.Text("late");
        });
    }

    private sealed class UnicodeServer : HttpServer
    {
        public UnicodeServer() : base([IPAddress.Loopback])
        {
        }

        protected override void Init() => Register("GET", "/cn",
            _ => Task.FromResult(HttpRouteResponse.Text("公告", "text/plain", Encoding.UTF8)));
    }
}
