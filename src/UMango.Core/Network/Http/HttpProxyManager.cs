// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/IO/Net/Http/HttpProxyManager.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.IO.Net.Http → UMango.Core.Network.Http。
//   2. **去掉 Windows 注册表**（原版监听 HKEY_CURRENT_USER\...\Internet Settings
//      的变更来自动刷新系统代理）。理由有二：
//        · Core 的 net10.0 支是"跨平台可编译"的证明，注册表会直接破坏它；
//        · 真正跨平台的系统代理来源是 WebRequest.DefaultWebProxy（.NET 自己
//          在各平台上处理），它不依赖注册表。
//      因此这里改为**按需读取**系统代理，而不是靠注册表变更事件。
//   3. 保留原版的代理字符串解析（"http=addr;socks=addr" / "http://addr" / "addr:port"）
//      与三种模式（NoProxy / SystemProxy / CustomProxy）。
//   4. 原版注册表监视器（Utils/OS/RegistryChangeMonitor.cs）随之不需要迁移。

using System.Net;

namespace UMango.Core.Network.Http;

/// <summary>代理使用方式。</summary>
public enum ProxyMode
{
    /// <summary>不使用代理。</summary>
    NoProxy,
    /// <summary>跟随系统代理设置。</summary>
    SystemProxy,
    /// <summary>使用用户手动填写的代理地址。</summary>
    CustomProxy
}

/// <summary>代理协议。</summary>
public enum ProxyProtocol
{
    Http,
    Socks
}

/// <summary>一条代理记录。</summary>
public readonly record struct ProxyItem(ProxyProtocol Protocol, string Address);

/// <summary>
/// 代理管理器。
///
/// 系统代理的读取走 .NET 的 <see cref="WebRequest.DefaultWebProxy"/>，
/// 跨平台可用；PCL 原版监听 Windows 注册表是因为它要在 Windows 上做实时刷新，
/// 代价是让整个模块变成 Windows-only。
/// </summary>
public sealed class HttpProxyManager : IWebProxy, IDisposable
{
    private readonly object _lock = new();
    private ProxyMode _mode = ProxyMode.SystemProxy;
    private readonly WebProxy _customWebProxy = new() { BypassProxyOnLocal = true };
    private readonly WebProxy _systemWebProxy = new() { BypassProxyOnLocal = true };

    public static readonly HttpProxyManager Instance = new();

    public ProxyMode Mode
    {
        get { lock (_lock) return _mode; }
        set
        {
            lock (_lock)
            {
                _mode = value;
                if (value == ProxyMode.SystemProxy) RefreshSystemProxy();
            }
        }
    }

    /// <summary>自定义代理地址（用户填的那一串）。</summary>
    public string? CustomProxyUrl { get; set; }

    /// <summary>本机直连的地址（127.0.0.1 / localhost）不走代理。</summary>
    public bool BypassProxyOnLocal
    {
        get { lock (_lock) return _customWebProxy.BypassProxyOnLocal; }
        set
        {
            lock (_lock)
            {
                _customWebProxy.BypassProxyOnLocal = value;
                _systemWebProxy.BypassProxyOnLocal = value;
            }
        }
    }

    public HttpProxyManager()
    {
        RefreshSystemProxy();
    }

    /// <summary>重新读取系统代理设置。</summary>
    public void RefreshSystemProxy()
    {
        lock (_lock)
        {
            try
            {
                var system = WebRequest.DefaultWebProxy;
                var probe = new Uri("https://www.example.com");
                var target = system?.GetProxy(probe);
                // GetProxy 原样返回目标 => 这台机器没有配置代理
                if (target is not null && target.Host == probe.Host && target.Port == probe.Port)
                {
                    _systemWebProxy.Address = null;
                    return;
                }
                _systemWebProxy.Address = target;
            }
            catch
            {
                // 读取系统代理失败（例如在没有网络配置的沙箱里）不是致命错误 —— 退化为直连。
                _systemWebProxy.Address = null;
            }
        }
    }

    public ICredentials? Credentials
    {
        get { lock (_lock) return _customWebProxy.Credentials; }
        set { lock (_lock) _customWebProxy.Credentials = value; }
    }

    public Uri? GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_lock)
        {
            switch (_mode)
            {
                case ProxyMode.NoProxy:
                    return destination;
                case ProxyMode.CustomProxy:
                    return _customWebProxy.GetProxy(destination);
                default:
                    return _systemWebProxy.GetProxy(destination);
            }
        }
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_lock)
        {
            switch (_mode)
            {
                case ProxyMode.NoProxy:
                    return true;
                case ProxyMode.CustomProxy:
                    return _customWebProxy.IsBypassed(host);
                default:
                    return _systemWebProxy.IsBypassed(host);
            }
        }
    }

    /// <summary>把用户填的代理字符串解析成代理记录列表。</summary>
    /// <remarks>
    /// 支持三种写法：
    /// <list type="bullet">
    ///   <item><c>http=192.168.1.100:8080;socks=192.168.1.100:1080</c>（分号分隔的键值对）</item>
    ///   <item><c>http://127.0.0.1:10808</c>（单条带协议）</item>
    ///   <item><c>127.0.0.1:10808</c>（单条裸地址）</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<ProxyItem> ParseProxyString(string? proxyString)
    {
        if (string.IsNullOrWhiteSpace(proxyString)) return [];

        if (proxyString.Contains('='))
        {
            var items = new List<ProxyItem>();
            foreach (var segment in proxyString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ParseKeyValueSegment(segment) is { } item) items.Add(item);
            }
            return items;
        }

        var single = ParseAddress(proxyString.Trim());
        return single is null ? [] : [single.Value];
    }

    /// <summary>解析 <c>protocol=address</c> 形式的段；格式非法时返回 null 以便忽略该段。</summary>
    private static ProxyItem? ParseKeyValueSegment(string segment)
    {
        var eqIndex = segment.IndexOf('=');
        if (eqIndex <= 0 || eqIndex >= segment.Length - 1) return null;

        return ParseAddress(segment[(eqIndex + 1)..].Trim(),
            segment[..eqIndex].Trim().Equals("socks", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Socks : ProxyProtocol.Http);
    }

    /// <summary>解析单个代理地址。</summary>
    private static ProxyItem? ParseAddress(string address, ProxyProtocol? protocol = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        address = address.Trim();

        if (address.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
        {
            address = address["socks://".Length..];
            protocol = ProxyProtocol.Socks;
        }
        else if (address.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
        {
            address = address["socks5://".Length..];
            protocol = ProxyProtocol.Socks;
        }
        else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            address = address["http://".Length..];
            protocol = ProxyProtocol.Http;
        }
        else if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            address = address["https://".Length..];
            protocol = ProxyProtocol.Http;
        }

        // 裸地址要补上协议才能被 Uri 解析
        if (!address.Contains("://")) address = "http://" + address;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        // 主机名必须是像样的主机：含点（域名/IP）、或者是 localhost、或者带端口。
        // 否则 "y=1" 这种段会被解析成 "http://1/"（Uri 会把纯数字主机规范成 0.0.0.1），
        // 得到一个毫无意义的代理项。
        // 注意这里比的是**原始字符串**里的主机部分，不是 uri.Host ——
        // Uri 已经把纯数字主机改写成 0.0.0.1，比 uri.Host 就发现不了问题了。
        var hostPart = address;
        var slash = hostPart.IndexOf('/');
        if (slash >= 0) hostPart = hostPart[..slash];
        var colon = hostPart.LastIndexOf(':');
        if (colon >= 0) hostPart = hostPart[..colon];

        var hasPort = !uri.IsDefaultPort;
        var hostLooksReal = hostPart.Contains('.') ||
                            hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            hasPort;
        if (!hostLooksReal) return null;

        return new ProxyItem(protocol ?? ProxyProtocol.Http, uri.ToString());
    }

    /// <summary>把代理项转成 .NET 能用的 WebProxy。</summary>
    public static WebProxy ToWebProxy(ProxyItem item)
        => new(item.Address) { BypassProxyOnLocal = true };

    public void Dispose()
    {
        // 没有需要释放的资源（没有注册表监视器、没有 socket）。
    }
}
