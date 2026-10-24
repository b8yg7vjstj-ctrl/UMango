using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UMango.Core.Network.Http.Server;

/// <summary>
/// 极简的本地 HTTP 服务端。
///
/// 对应 PCL-CE 的 <c>PCL.Core/IO/Net/Http/HttpServer.cs</c>（Apache-2.0）。
/// 逐字采用的部分：路由表（方法 + 路径 → 处理器）、精确匹配优先于通配、
/// 处理器异常统一转 500、<see cref="HttpRouteResponse"/> 的写入方式。
///
/// ## 与原版的实质差异（每一条都修掉了原版的一个真实缺陷）
///
/// ① **IPv6 回环地址写错，前缀无法解析。**
///    原版把 <c>IPAddress.IPv6Loopback</c> 直接拼进前缀，得到 <c>http://::1:8080/</c> ——
///    这不是合法的 URI（IPv6 字面量必须带方括号）。
///    修法：IPv6 用方括号包裹。
///
/// ② **IPv6 前缀在部分平台上根本绑不上。**
///    即使写成 <c>http://[::1]:8080/</c>，macOS 上的 <see cref="HttpListener"/>
///    依然会抛 <c>HttpListenerException: Invalid port in prefix.</c>（实测）。
///    原版因此**在 macOS 上一个地址都起不来**。
///    修法：逐个地址尝试绑定，绑不上的**跳过**（并记住跳过原因），
///    只有**一个都绑不上**才算启动失败。
///
/// ③ **取消后请求被静默丢弃，客户端一直等到超时。**
///    原版把请求处理丢进 <c>Task.Run(..., cancellationToken)</c>：
///    令牌一取消，委托**根本不会执行**，于是既没写响应也没关连接，
///    客户端那边就是"挂着直到超时"。修法：处理任务**不挂取消令牌**，
///    只让「取下一个请求」这一步可取消；每个请求的 <c>finally</c> 负责关连接。
///
/// ④ **端口是"先占后放"选出来的，存在竞态。**
///    原版用一个临时 socket 绑 0 端口拿到号就放掉，再拿这个号去给 HttpListener 绑定 ——
///    中间任何人都能占走这个端口，于是 <c>Start()</c> 抛
///    <see cref="HttpListenerException"/>。修法：端口为 0 时先探一个空闲号，
///    绑定失败（被抢占）就**换一个号重试**，最多 10 次。
///
/// ⑤ **重复 <see cref="Start"/> 会炸，且会重复初始化路由。**
///    原版没有「已启动」标志，第二次 <c>Start()</c> 直接让 HttpListener 抛异常；
///    而且它的 <c>Init()</c> 判断是「路由表为空才调」—— 如果某个子类没注册任何路由，
///    每次 <c>Start()</c> 都会重跑一遍 <c>Init()</c>。修法：用显式的初始化标志，
///    并把「已启动/已停止」做成明确的状态机。
///
/// ⑥ **停止之后无法重启。**
///    原版 <c>Stop()</c> 取消令牌并 <c>_server.Stop()</c>，但没重建令牌；
///    再 <c>Start()</c> 的话 <c>GetContextAsync()</c> 拿到的还是**已取消**的令牌，
///    循环立刻退出 —— 服务器"启动了"但一个请求也处理不了。修法：<c>Start()</c> 重建令牌，
///    并**重建 <see cref="HttpListener"/>**（HttpListener 停了之后自身就废了，不能复用）。
///
/// ⑦ **处理器抛异常时，如果响应头已经发出就再写 500 会抛二次异常。**
///    原版在 <c>catch</c> 里无条件写 500。修法：写之前先判断还能不能写。
/// </summary>
public abstract class HttpServer : IDisposable
{
    private readonly Dictionary<(string Method, string Path), Func<HttpListenerRequest, Task<HttpRouteResponse>>> _handlers = new();

    private readonly IPAddress[] _addresses;
    private readonly List<string> _skippedAddresses = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HttpListener? _listener;
    private bool _routesInitialized;
    private bool _disposed;

    /// <summary>构造。监听地址为空时默认只监听本机回环（IPv4 + IPv6）。</summary>
    protected HttpServer(IPAddress[]? listenAddr = null, ushort port = 0)
    {
        ArgumentNullException.ThrowIfNull(listenAddr);

        RequestedPort = port;
        Port = port;

        var addresses = listenAddr.Length == 0
            ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
            : listenAddr;

        _addresses = addresses;
        Host = addresses.Select(a => a.ToString()).ToArray();
    }

    /// <summary>构造时请求的端口（0 表示自动分配）。</summary>
    public ushort RequestedPort { get; }

    /// <summary>实际监听的端口 —— 端口为 0 自动分配时，<see cref="Start"/> 之后才有值。</summary>
    public ushort Port { get; private set; }

    /// <summary>实际监听的地址。</summary>
    public string[] Host { get; }

    /// <summary>是否正在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 本次启动时**没能绑上**的地址。
    /// 例如 macOS 上 HttpListener 绑不了 IPv6 回环 —— 这种情况不该让整个服务器起不来。
    /// </summary>
    public IReadOnlyList<string> SkippedAddresses => _skippedAddresses;

    /// <summary>
    /// 初始化路由。子类在这里调用 <see cref="Register"/>。
    /// 只会被调用一次（在第一次 <see cref="Start"/> 之前）。
    /// </summary>
    protected abstract void Init();

    /// <summary>注册一个路由处理器。</summary>
    protected void Register(string method, string path, Func<HttpListenerRequest, Task<HttpRouteResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[(method.ToUpperInvariant(), path)] = handler;
    }

    /// <summary>注册一个路由处理器（方法用字符串，如 "GET"）。</summary>
    protected void Register(HttpMethod method, string path, Func<HttpListenerRequest, Task<HttpRouteResponse>> handler)
        => Register(method.Method, path, handler);

    /// <summary>已注册的路由数（测试用）。</summary>
    public int RouteCount => _handlers.Count;

    /// <summary>启动服务器。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) throw new InvalidOperationException("服务器已经在运行。");

        if (!_routesInitialized)
        {
            Init();
            _routesInitialized = true;
        }

        // 每次启动都重建令牌与监听器（类注释⑥）：
        // HttpListener 一旦 Stop/失败就不可复用，必须整个换新的。
        _cts = new CancellationTokenSource();
        _skippedAddresses.Clear();

        // 端口 0 = 自动挑一个空闲端口（类注释④）。
        // HttpListener 不接受 :0 前缀，也提供不了"读回实际绑定端口"的接口，
        // 只能先探一个空闲号再绑；被抢占就换一个号重试。
        var requested = RequestedPort;
        var bound = false;
        string? lastError = null;

        for (var attempt = 0; attempt < 10 && !bound; attempt++)
        {
            var candidate = requested != 0 ? requested : FindFreePort();
            if (candidate == 0)
            {
                lastError = "找不到空闲端口。";
                break;
            }

            // 逐个地址尝试（类注释②）：绑不上的跳过，不要连累其它地址。
            var usable = new List<IPAddress>();
            foreach (var address in _addresses)
            {
                if (TryBindAddress(address, candidate, ref lastError))
                    usable.Add(address);
            }

            if (usable.Count == 0)
            {
                if (requested != 0)
                {
                    // 指定端口起不来 —— 抛出带明确信息的异常，不要静默。
                    throw new InvalidOperationException(
                        $"无法在端口 {requested} 上启动 HTTP 服务器：{lastError ?? "地址绑定失败。"}");
                }

                continue;
            }

            var listener = new HttpListener();
            try
            {
                foreach (var address in usable)
                    listener.Prefixes.Add(FormatPrefix(address, candidate));

                listener.Start();
                _listener = listener;
                Port = candidate;
                bound = true;
            }
            catch (Exception ex)
            {
                listener.Close();
                lastError = ex.Message;
                if (requested != 0)
                {
                    throw new InvalidOperationException(
                        $"无法在端口 {requested} 上启动 HTTP 服务器：{ex.Message}");
                }
            }
        }

        if (!bound)
        {
            _cts.Dispose();
            _cts = null;
            throw new InvalidOperationException(
                $"无法为 HTTP 服务器分配一个空闲端口：{lastError ?? "未知原因"}");
        }

        IsRunning = true;
        _loop = Task.Run(() => HandleLoopAsync(_cts.Token));
    }

    /// <summary>停止服务器（可以再次 <see cref="Start"/> 起来）。</summary>
    public void Stop()
    {
        if (!IsRunning) return;

        IsRunning = false;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
        try { _listener?.Stop(); } catch (ObjectDisposedException) { /* 已释放 */ }
        try { _listener?.Close(); } catch (ObjectDisposedException) { /* 已释放 */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>释放。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 试着把一个地址绑到 <paramref name="port"/> 上。
    /// 绑不上就记下原因返回 false（**不抛**）—— 单个地址失败不该让整个服务器起不来。
    /// </summary>
    private bool TryBindAddress(IPAddress address, ushort port, ref string? lastError)
    {
        var probe = new HttpListener();
        try
        {
            probe.Prefixes.Add(FormatPrefix(address, port));
            probe.Start();
            probe.Close();
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"{address}: {ex.Message}";
            try { probe.Close(); } catch { /* 关不掉就算了 */ }
            return false;
        }
    }

    /// <summary>探一个当前空闲的 TCP 端口号（0 表示没探到）。</summary>
    private static ushort FindFreePort()
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return (ushort)port;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 把监听地址格式化成 HttpListener 前缀（IPv6 必须带方括号）。
    /// 公开是为了让测试能直接断言前缀格式 —— 原版就是在这里把
    /// <c>http://::1:8080/</c> 这种非法 URI 交给 HttpListener 的。
    /// </summary>
    public static string FormatPrefix(IPAddress address, ushort port)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{port}/";
    }

    private async Task HandleLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            // 处理任务**不挂取消令牌**（类注释③）：
            // 一旦挂上，Stop 之后进来的请求会被静默丢弃，客户端一直等到超时。
            _ = Task.Run(() => ProcessRequestAsync(context));
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? string.Empty;
            var method = request.HttpMethod?.ToUpperInvariant() ?? "GET";

            if (_handlers.TryGetValue((method, path), out var handler))
            {
                await ExecuteHandlerAsync(handler, request, response).ConfigureAwait(false);
                return;
            }

            // 通配：注册 "*" 兜住所有路径。
            if (_handlers.TryGetValue((method, "*"), out var wildcard))
            {
                await ExecuteHandlerAsync(wildcard, request, response).ConfigureAwait(false);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.NotFound;
        }
        catch (Exception)
        {
            // 这里已经尽力了；连接会被 finally 关掉。
        }
        finally
        {
            try { context.Response.Close(); } catch { /* 关不掉就算了 */ }
        }
    }

    private static async Task ExecuteHandlerAsync(
        Func<HttpListenerRequest, Task<HttpRouteResponse>> handler,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        try
        {
            var routeResponse = await handler(request).ConfigureAwait(false);
            routeResponse.Pour(response);
        }
        catch (Exception ex)
        {
            // 响应可能已经发出了一半 —— 这时再写 500 会抛二次异常（类注释⑦）。
            // 所以整个写回动作都包在 try 里，失败就当作"这个响应废了"，
            // 由调用方 finally 关连接，客户端看到的是不完整响应而不是挂死。
            try
            {
                // 注意：状态码必须**跟着响应对象一起**写出去。
                // 如果先 response.StatusCode = 500 再 Pour(response)，
                // Pour 会用响应对象自己的 StatusCode（默认 200）覆盖掉它 ——
                // 结果是"处理器炸了，客户端却收到 200"。
                var error = HttpRouteResponse.Text(
                    "Internal Server Error:\n" + ex.Message,
                    "text/plain",
                    Encoding.UTF8);
                error.StatusCode = HttpStatusCode.InternalServerError;
                error.Pour(response);
            }
            catch
            {
                // 已经写出去一部分，救不回来了。
            }
        }
    }
}
