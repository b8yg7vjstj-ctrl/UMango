// SPDX-License-Identifier: Apache-2.0
//
// 本文件的代理地址解析部分参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/IO/Net/Http/HttpProxyManager.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.Network；
//   2. **剥离 Windows 注册表**：原版的系统代理读取依赖
//      `Microsoft.Win32.Registry` 与 `RegistryChangeMonitor`（Windows 专属），
//      UMango 的 Core 必须在 net10.0 支编译（跨平台性由编译期证明），
//      因此系统代理读取改为可注入的 `ISystemProxyProbe`；
//   3. 原版的解析函数是私有静态，这里拆成独立的 `ProxyAddressParser`
//      （可单测），并把 `ProxyProtocol`/`ProxyItem` 提为公共类型；
//   4. 原版 `GetProxy` 返回 `WebProxy.GetProxy` 的结果（可能返回目标本身表示"直连"），
//      这里保持一致：返回 null 表示不使用代理。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;

namespace UMango.Core.Network;

/// <summary>代理模式。</summary>
public enum ProxyMode
{
    /// <summary>不使用代理。</summary>
    NoProxy = 0,

    /// <summary>跟随系统代理设置。</summary>
    SystemProxy = 1,

    /// <summary>使用自定义代理。</summary>
    CustomProxy = 2,
}

/// <summary>代理协议。</summary>
public enum ProxyProtocol
{
    Http,
    Socks,
}

/// <summary>一个解析出来的代理项。</summary>
/// <param name="Protocol">协议。</param>
/// <param name="Address">地址，规范化成 <c>host:port</c>。</param>
public readonly record struct ProxyItem(ProxyProtocol Protocol, string Address);

/// <summary>
/// 代理地址解析。
///
/// 支持两种常见写法：
///   1. 键值形式：<c>http=192.168.1.100:8080;socks=192.168.1.100:1080</c>
///   2. 单地址形式：<c>http://127.0.0.1:1145</c> 或 <c>127.0.0.1:1145</c>
/// </summary>
public static class ProxyAddressParser
{
    /// <summary>解析一段代理设置文本，返回全部有效项；无法识别的段被忽略。</summary>
    public static IReadOnlyList<ProxyItem> Parse(string? proxyString)
    {
        if (string.IsNullOrWhiteSpace(proxyString))
            return [];

        // 含 '=' 的形式：键值对，可以一次给出多个协议
        if (proxyString.Contains('=', StringComparison.Ordinal))
        {
            var items = new List<ProxyItem>();
            foreach (var segment in proxyString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var item = ParseKeyValueSegment(segment);
                if (item is not null)
                    items.Add(item.Value);
            }
            return items;
        }

        // 单地址形式
        var single = ParseAddress(proxyString.Trim());
        return single is null ? [] : [single.Value];
    }

    /// <summary>解析 <c>protocol=address</c> 形式的段；格式非法时返回 null 以便忽略该段。</summary>
    public static ProxyItem? ParseKeyValueSegment(string segment)
    {
        var eqIndex = segment.IndexOf('=', StringComparison.Ordinal);
        // 缺少 '='、协议名或地址的段视为非法，直接忽略
        if (eqIndex <= 0 || eqIndex >= segment.Length - 1)
            return null;

        return ParseAddress(
            segment[(eqIndex + 1)..].Trim(),
            ParseProtocol(segment[..eqIndex].Trim()));
    }

    /// <summary>
    /// 解析单个代理地址。
    /// 支持带 scheme 的地址（如 <c>http://127.0.0.1:10808</c>）与纯 <c>host:port</c> 地址。
    /// </summary>
    public static ProxyItem? ParseAddress(string address, ProxyProtocol? protocol = null)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        // 能解析出主机名的地址，规范化为 host:port
        if (Uri.TryCreate(address, new UriCreationOptions(), out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var hostPort = uri.Port > 0
                ? FormattableString.Invariant($"{uri.Host}:{uri.Port}")
                : uri.Host;
            return new ProxyItem(protocol ?? ParseProtocol(uri.Scheme), hostPort);
        }

        // 纯 host:port 地址（无法作为 URI 解析），按指定协议（默认 Http）原样使用
        return new ProxyItem(protocol ?? ProxyProtocol.Http, address);
    }

    /// <summary>把协议名解析成枚举；不认识的一律当 Http。</summary>
    public static ProxyProtocol ParseProtocol(string scheme) =>
        scheme.ToLowerInvariant() switch
        {
            "socks" or "socks4" or "socks5" => ProxyProtocol.Socks,
            _ => ProxyProtocol.Http,
        };

    /// <summary>
    /// 从系统代理设置文本里挑出该用的代理项。
    /// 只取第一个 HTTP 代理 —— SOCKS 代理在 .NET 的 <see cref="WebProxy"/> 里不被支持，
    /// 选它只会让请求直接失败。
    /// </summary>
    public static ProxyItem? SelectHttpProxy(IReadOnlyList<ProxyItem> items) =>
        items.FirstOrDefault(static x => x.Protocol == ProxyProtocol.Http) is { } found && !string.IsNullOrEmpty(found.Address)
            ? found
            : null;
}

/// <summary>系统代理设置的读取器。实现由 App 层按平台提供。</summary>
public interface ISystemProxyProbe
{
    /// <summary>读取当前的系统代理设置。</summary>
    /// <param name="enabled">系统是否启用了代理。</param>
    /// <param name="proxyString">系统的代理设置文本（可为空）。</param>
    /// <returns>读取是否成功。</returns>
    bool TryRead(out bool enabled, out string? proxyString);
}

/// <summary>系统代理的「不启用」实现，作为默认与降级兜底。</summary>
public sealed class NoSystemProxyProbe : ISystemProxyProbe
{
    /// <inheritdoc />
    public bool TryRead(out bool enabled, out string? proxyString)
    {
        enabled = false;
        proxyString = null;
        return false;
    }
}

/// <summary>
/// 全局代理管理器持有者。
///
/// 为什么需要它：仓库里有些地方会自建 <see cref="System.Net.Http.HttpClient"/>
/// （例如内置下载引擎）。如果只有 <see cref="HttpService"/> 认代理，
/// 这些自建客户端就会绕过代理设置 —— 用户设了代理却对下载不生效。
/// 用一个全局持有者让所有 HTTP 出站走同一套代理策略。
/// </summary>
public static class ProxyHolder
{
    private static ProxyManager? _current;

    /// <summary>当前代理管理器；未设置时返回 null（表示不用代理）。</summary>
    public static ProxyManager? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>按当前代理配置创建一个 <see cref="System.Net.Http.HttpClientHandler"/>。</summary>
    public static System.Net.Http.HttpClientHandler CreateHandler()
    {
        var handler = new System.Net.Http.HttpClientHandler();
        var manager = Current;
        if (manager is null)
            return handler;

        handler.Proxy = manager;
        handler.UseProxy = manager.Mode != ProxyMode.NoProxy;
        if (manager.Mode == ProxyMode.CustomProxy && manager.Credentials is not null)
            handler.Credentials = manager.Credentials;
        return handler;
    }
}

/// <summary>
/// 代理管理器：按模式决定用哪个代理。
///
/// 与 PCL 原版的差异见本文件头部；核心区别是系统代理的读取被抽成接口，
/// 这样 Core 能在 macOS 上编译并测试，而平台相关的实现放在 App 层。
/// </summary>
public sealed class ProxyManager : IWebProxy, IDisposable
{
    private readonly object _lock = new();
    private readonly WebProxy _customWebProxy = new() { BypassProxyOnLocal = true };
    private readonly WebProxy _systemWebProxy = new() { BypassProxyOnLocal = true };
    private ProxyMode _mode = ProxyMode.SystemProxy;
    private ISystemProxyProbe _probe;

    /// <summary>创建代理管理器。</summary>
    /// <param name="probe">系统代理读取器；为空时使用「不启用」实现。</param>
    public ProxyManager(ISystemProxyProbe? probe = null)
    {
        _probe = probe ?? new NoSystemProxyProbe();
        RefreshSystemProxy();
    }

    /// <summary>当前模式。</summary>
    public ProxyMode Mode
    {
        get { lock (_lock) { return _mode; } }
        set { lock (_lock) { _mode = value; } }
    }

    /// <summary>自定义代理地址。</summary>
    public Uri? CustomProxyAddress
    {
        get { lock (_lock) { return _customWebProxy.Address; } }
        set { lock (_lock) { _customWebProxy.Address = value; } }
    }

    /// <summary>自定义代理凭据（用户名 + 密码）。</summary>
    public ICredentials? CustomProxyCredentials
    {
        get { lock (_lock) { return _customWebProxy.Credentials; } }
        set { lock (_lock) { _customWebProxy.Credentials = value; } }
    }

    /// <summary>是否绕过本地地址（localhost / 127.0.0.1 / 局域网）。</summary>
    public bool BypassOnLocal
    {
        get { lock (_lock) { return _bypassOnLocal; } }
        set
        {
            lock (_lock)
            {
                _bypassOnLocal = value;
                _systemWebProxy.BypassProxyOnLocal = value;
                _customWebProxy.BypassProxyOnLocal = value;
            }
        }
    }

    private bool _bypassOnLocal = true;

    /// <summary>系统代理当前是否可用（有地址且系统启用了代理）。</summary>
    public bool SystemProxyAvailable
    {
        get { lock (_lock) { return _systemWebProxy.Address is not null; } }
    }

    /// <summary>设置系统代理读取器并立即刷新。</summary>
    public void SetProbe(ISystemProxyProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (_lock) { _probe = probe; }
        RefreshSystemProxy();
    }

    /// <summary>从系统重新读取代理设置。</summary>
    public void RefreshSystemProxy()
    {
        lock (_lock)
        {
            try
            {
                var ok = _probe.TryRead(out var enabled, out var proxyString);
                var items = ok ? ProxyAddressParser.Parse(proxyString) : [];
                var selected = ProxyAddressParser.SelectHttpProxy(items);

                // 仅当系统代理已启用且存在有效的 HTTP 代理时才应用
                _systemWebProxy.Address = selected is null || !enabled
                    ? null
                    : new Uri("http://" + selected.Value.Address);
            }
            catch
            {
                // 读系统代理失败不应该让应用起不来 —— 退回到「系统代理不可用」。
                _systemWebProxy.Address = null;
            }
        }
    }

    /// <summary>取得目标地址应使用的代理；null 表示直连。</summary>
    public Uri? GetProxy(Uri destination)
    {
        lock (_lock)
        {
            return _mode switch
            {
                ProxyMode.NoProxy => null,
                ProxyMode.SystemProxy => _systemWebProxy.GetProxy(destination),
                ProxyMode.CustomProxy => _customWebProxy.GetProxy(destination),
                _ => null,
            };
        }
    }

    /// <summary>目标地址是否绕过代理。</summary>
    public bool IsBypassed(Uri host)
    {
        lock (_lock)
        {
            return _mode switch
            {
                ProxyMode.NoProxy => true,
                ProxyMode.SystemProxy => _systemWebProxy.IsBypassed(host),
                ProxyMode.CustomProxy => _customWebProxy.IsBypassed(host),
                _ => true,
            };
        }
    }

    /// <summary>凭据。只在自定义代理模式下返回。</summary>
    public ICredentials? Credentials
    {
        get
        {
            lock (_lock)
            {
                return _mode == ProxyMode.CustomProxy ? _customWebProxy.Credentials : null;
            }
        }
        set
        {
            lock (_lock) { _customWebProxy.Credentials = value; }
        }
    }

    /// <summary>
    /// 生成供 <see cref="System.Net.Http.HttpClientHandler"/> 使用的代理 URL。
    /// 返回 null 表示不使用代理。
    /// </summary>
    public string? GetProxyUrl()
    {
        lock (_lock)
        {
            return _mode switch
            {
                ProxyMode.NoProxy => null,
                ProxyMode.SystemProxy => _systemWebProxy.Address?.ToString(),
                ProxyMode.CustomProxy => _customWebProxy.Address?.ToString(),
                _ => null,
            };
        }
    }

    /// <summary>释放。保留供将来挂平台监听器（Windows 注册表变更监控等）。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
