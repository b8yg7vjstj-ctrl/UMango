// SPDX-License-Identifier: Apache-2.0
//
// DNS over HTTPS（DoH，RFC 8484）解析器。
//
// **与 PCL-CE 的关系**
// PCL 的 `PCL.Core/IO/Net/Dns/DnsQuery.cs`（Apache-2.0）用第三方包 `Ae.Dns.Protocol`
// 的 `DnsCachingClient` + `WeightedDnsRacerClient` 做 DoH 查询，
// 固定三个上游：`doh.pub` / `doh.pysio.online` / `cloudflare-dns.com`，
// 并用 `MemoryCache` 做缓存。
//
// UMango 的差异：
//   · **不引入 `Ae.Dns` 依赖** → 自己实现 RFC 1035 线格式（`DnsWire.cs`）与 DoH 传输；
//   · **上游地址可配置**，默认一个都不内置 —— 也就是说**不访问任何默认地址**；
//     PCL 写死的三个国内公共 DoH 在 UMango 里不被默认启用（隐私与可用性考量，
//     且公共 DoH 会被第三方看到所有查询）；
//   · 缓存自己实现（PCL 依赖 `System.Runtime.Caching`，UMango 走 Core 自己的 TTL 缓存）。
//
// **失败回退**：与 PCL 一致 —— DoH 全部失败时回落到系统解析器
// （`System.Net.Dns.GetHostAddressesAsync`），并在结果里标注来源。
//
// 参考：PCL-CE `PCL.Core/IO/Net/Dns/DnsQuery.cs`（Apache-2.0，只借鉴语义，独立实现）。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Network.Dns;

/// <summary>地址解析结果的来源。</summary>
public enum DnsResolveSource
{
    /// <summary>没有解析出任何地址。</summary>
    None,

    /// <summary>来自 DoH 上游。</summary>
    Doh,

    /// <summary>来自系统解析器（DoH 失败后的回落）。</summary>
    System,
}

/// <summary>一次地址解析的结果。</summary>
/// <param name="Host">被解析的主机名。</param>
/// <param name="Addresses">解析出的地址（IPv4 在前，IPv6 在后）。</param>
/// <param name="Source">地址来源。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
public readonly record struct DnsResolveResult(
    string Host,
    IReadOnlyList<IPAddress> Addresses,
    DnsResolveSource Source,
    string? Error)
{
    /// <summary>是否拿到了至少一个地址。</summary>
    public bool HasAddresses => Addresses.Count > 0;
}

/// <summary>DoH 传输契约（便于测试时替换成假实现）。</summary>
public interface IDohTransport
{
    /// <summary>
    /// 发送一次 DoH 查询。
    /// </summary>
    /// <param name="url">DoH 上游地址（形如 <c>https://host/dns-query</c>）。</param>
    /// <param name="query">RFC 1035 查询报文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答报文；失败时返回 null。</returns>
    Task<byte[]?> QueryAsync(string url, byte[] query, CancellationToken ct);
}

/// <summary>基于 <see cref="HttpClient"/> 的 DoH 传输（RFC 8484 的 POST 形式）。</summary>
public sealed class HttpDohTransport : IDohTransport
{
    private readonly HttpClient _client;

    /// <summary>用给定的客户端构造。</summary>
    public HttpDohTransport(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public async Task<byte[]?> QueryAsync(string url, byte[] query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");

            using var response = await _client.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 传输层异常一律当成"这个上游不可用"，让上层去试下一个 / 回落系统解析器。
            return null;
        }
    }
}

/// <summary>DoH 解析器：多上游竞速 + TTL 缓存 + 系统回落。</summary>
public sealed class DohResolver
{
    private readonly IDohTransport _transport;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly Random _random = new();

    /// <summary>构造解析器。</summary>
    /// <param name="transport">传输层（生产用 <see cref="HttpDohTransport"/>，测试用假实现）。</param>
    public DohResolver(IDohTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>DoH 上游地址列表（**默认为空** —— 不访问任何默认地址）。</summary>
    public IReadOnlyList<string> Upstreams { get; set; } = Array.Empty<string>();

    /// <summary>缓存生存时间（秒）。</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>单次上游查询超时（毫秒）。</summary>
    public int QueryTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// 时钟来源（默认 <see cref="DateTimeOffset.UtcNow"/>）。
    /// 做成可替换的是为了**能测缓存过期** —— 否则测试要么真等一秒，要么测不到过期分支。
    /// </summary>
    public Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>是否在 DoH 全部失败时回落到系统解析器（与 PCL 一致，默认开）。</summary>
    public bool FallBackToSystem { get; set; } = true;

    /// <summary>查询次数统计（便于界面展示与测试）。</summary>
    public int QueryCount { get; private set; }

    /// <summary>命中缓存的次数统计。</summary>
    public int CacheHitCount { get; private set; }

    /// <summary>
    /// 解析主机名的地址。
    /// </summary>
    /// <param name="host">主机名。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<DnsResolveResult> ResolveAsync(string host, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        if (TryGetCached(host, out var cached))
        {
            CacheHitCount++;
            return new DnsResolveResult(host, cached, DnsResolveSource.Doh, null);
        }

        var addresses = await QueryDohAsync(host, ct).ConfigureAwait(false);
        if (addresses.Count > 0)
        {
            Cache(host, addresses);
            return new DnsResolveResult(host, addresses, DnsResolveSource.Doh, null);
        }

        if (FallBackToSystem)
        {
            try
            {
                var system = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                if (system.Length > 0)
                {
                    return new DnsResolveResult(host, system, DnsResolveSource.System, null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DnsResolveResult(host, Array.Empty<IPAddress>(), DnsResolveSource.None, ex.Message);
            }
        }

        return new DnsResolveResult(host, Array.Empty<IPAddress>(), DnsResolveSource.None, "DoH 未返回可用地址");
    }

    /// <summary>清空缓存。</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>向所有上游并发查询，合并结果（IPv4 在前）。</summary>
    private async Task<IReadOnlyList<IPAddress>> QueryDohAsync(string host, CancellationToken ct)
    {
        var upstreams = Upstreams;
        if (upstreams.Count == 0)
        {
            return Array.Empty<IPAddress>();
        }

        QueryCount++;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(QueryTimeoutMs);

        // 并发问所有上游，谁先回用谁；全部回来的结果合并去重（多上游能提高成功率）。
        var tasks = upstreams.Select(async url =>
        {
            try
            {
                var query = DnsWire.BuildQuery(host, DnsRecordType.A, NextTransactionId());
                var v4 = await _transport.QueryAsync(url, query, timeoutCts.Token).ConfigureAwait(false);
                var v4Addrs = v4 is null
                    ? Array.Empty<IPAddress>()
                    : DnsWire.ParseAnswers(v4)
                        .Where(r => r.Type == DnsRecordType.A)
                        .Select(r => r.Address)
                        .ToArray();

                var queryV6 = DnsWire.BuildQuery(host, DnsRecordType.Aaaa, NextTransactionId());
                var v6 = await _transport.QueryAsync(url, queryV6, timeoutCts.Token).ConfigureAwait(false);
                var v6Addrs = v6 is null
                    ? Array.Empty<IPAddress>()
                    : DnsWire.ParseAnswers(v6)
                        .Where(r => r.Type == DnsRecordType.Aaaa)
                        .Select(r => r.Address)
                        .ToArray();

                return v4Addrs.Concat(v6Addrs).ToArray();
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<IPAddress>();
            }
            catch (Exception)
            {
                return Array.Empty<IPAddress>();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var merged = new List<IPAddress>();
        foreach (var group in results)
        {
            foreach (var address in group)
            {
                if (!merged.Contains(address))
                {
                    merged.Add(address);
                }
            }
        }

        // IPv4 在前，IPv6 在后（与 PCL 的 QueryForIpAsync 一致）
        return merged.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
    }

    /// <summary>取一个 16 位事务 ID。</summary>
    private ushort NextTransactionId()
    {
        lock (_random)
        {
            return (ushort)_random.Next(1, 65536);
        }
    }

    /// <summary>读缓存。</summary>
    private bool TryGetCached(string host, out IReadOnlyList<IPAddress> addresses)
    {
        addresses = Array.Empty<IPAddress>();

        if (!_cache.TryGetValue(host, out var entry))
        {
            return false;
        }

        if (Clock() - entry.StoredAt < TimeSpan.FromSeconds(Math.Max(1, CacheTtlSeconds)))
        {
            addresses = entry.Addresses;
            return true;
        }

        _cache.TryRemove(host, out _);
        return false;
    }

    /// <summary>写缓存。</summary>
    private void Cache(string host, IReadOnlyList<IPAddress> addresses) =>
        _cache[host] = new CacheEntry(addresses, Clock());

    /// <summary>缓存条目。</summary>
    private sealed record CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTimeOffset StoredAt);
}
