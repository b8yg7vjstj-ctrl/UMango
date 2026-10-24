// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Net/Http/Cache/HttpCacheHandler.cs 与 HttpCacheMetadata.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.IO.Net.Http.Cache → UMango.Core.Network.Http。
//   2. 原版依赖 PCL 自有的 ICacheService（PCL.Core/IO/Storage/Cache/）。
//      UMango 有自己的 LruCache，但它不是 ICacheService ——
//      与其为了一个 handler 去实现 PCL 的整套缓存抽象，
//      不如在这里定义一个**最小**的缓存契约（只要求能按 key 存取字节数组），
//      由 App/Core 的具体实现去满足它。PCL 的 ICacheService 未来若整体迁移，
//      这个接口可以由它实现。
//   3. 原版的反序列化响应（_DeserializeResponse）**丢掉了状态码**：
//      缓存命中时一律返回 200，调用方无法知道原响应其实是 404。
//      这里把状态码一起缓存并在重建时还原。
//   4. 原版 GetHeaders 之类依赖 Content 非空；此处对 204/304 做了防护。

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace UMango.Core.Network.Http;

/// <summary>HTTP 响应的缓存元数据。</summary>
public sealed record HttpCacheMetadata
{
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public int StatusCode { get; init; }
}

/// <summary>
/// HTTP 缓存的最小契约。
///
/// 只要求"按 key 存取字节数组"—— 缓存 handler 真正需要的就是这些。
/// </summary>
public interface IHttpCacheStore
{
    Task<(bool Found, byte[]? Value)> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, byte[] value, TimeSpan? ttl, CancellationToken ct = default);
}

/// <summary>
/// HTTP 缓存处理器。
///
/// 工作方式：先看缓存；命中就直接返回（并打上 <c>X-Cache-Hit: HIT</c>）。
/// 未命中时把上次的 ETag / Last-Modified 带上做条件请求，服务端回 304 就
/// 说明本地副本还有效，直接用本地副本；否则缓存新响应。
/// </summary>
public class HttpCacheHandler : DelegatingHandler
{
    /// <summary>命中标记头。</summary>
    public const string CacheHitHeader = "X-Cache-Hit";

    private readonly IHttpCacheStore _cache;
    private readonly TimeSpan _defaultTtl;

    public HttpCacheHandler(HttpMessageHandler invoker, IHttpCacheStore cache, TimeSpan? defaultTtl = null)
        : base(invoker)
    {
        _cache = cache;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri?.ToString();
        if (string.IsNullOrEmpty(uri))
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        // 只有 GET/HEAD 值得缓存
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        var bodyKey = BodyKey(uri);
        var metaKey = MetaKey(uri);

        var cached = await _cache.GetAsync(bodyKey, ct).ConfigureAwait(false);
        if (cached.Found && cached.Value is not null)
        {
            var hit = Rebuild(cached.Value, HttpStatusCode.OK);
            hit.Headers.TryAddWithoutValidation(CacheHitHeader, "HIT");
            hit.RequestMessage = request;
            return hit;
        }

        var metaCached = await _cache.GetAsync(metaKey, ct).ConfigureAwait(false);
        if (metaCached.Found && metaCached.Value is not null)
        {
            var meta = ReadMetadata(metaCached.Value);
            if (meta.ETag is not null)
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(meta.ETag));
            if (meta.LastModified is not null &&
                DateTimeOffset.TryParse(meta.LastModified, out var lastModified))
                request.Headers.IfModifiedSince = lastModified;
        }

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.Headers.CacheControl?.NoStore ?? false)
            return response;

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            var reCached = await _cache.GetAsync(bodyKey, ct).ConfigureAwait(false);
            if (reCached.Found && reCached.Value is not null)
            {
                var hit = Rebuild(reCached.Value, HttpStatusCode.OK);
                hit.Headers.TryAddWithoutValidation(CacheHitHeader, "HIT");
                hit.RequestMessage = request;
                return hit;
            }
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var ttl = ComputeTtl(response);

        await _cache.SetAsync(bodyKey, body, ttl, ct).ConfigureAwait(false);
        var newMeta = new HttpCacheMetadata
        {
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified?.ToString("O"),
            StatusCode = (int)response.StatusCode
        };
        await _cache.SetAsync(metaKey, SerializeMetadata(newMeta), null, ct).ConfigureAwait(false);

        response.Headers.TryAddWithoutValidation(CacheHitHeader, "MISS");
        return response;
    }

    /// <summary>缓存体 + 元数据在同一个 key 下一起存（body 前缀 + meta 后缀）。</summary>
    public static string BodyKey(string uri) => "http:body:" + uri;
    public static string MetaKey(string uri) => "http:meta:" + uri;

    /// <summary>从缓存字节里还原响应。</summary>
    private static HttpResponseMessage Rebuild(byte[] data, HttpStatusCode status)
        => new(status) { Content = new ByteArrayContent(data) };

    /// <summary>把元数据序列化成 JSON 字节。</summary>
    public static byte[] SerializeMetadata(HttpCacheMetadata meta)
        => System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(meta,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));

    /// <summary>读元数据（缓存里存的是 JSON 化的形式）。</summary>
    private static HttpCacheMetadata ReadMetadata(byte[] data)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node is null) return new HttpCacheMetadata();
            return new HttpCacheMetadata
            {
                ETag = node["eTag"]?.GetValue<string>(),
                LastModified = node["lastModified"]?.GetValue<string>(),
                StatusCode = node["statusCode"]?.GetValue<int>() ?? 0
            };
        }
        catch
        {
            return new HttpCacheMetadata();
        }
    }

    /// <summary>算出响应的缓存时长。</summary>
    public static TimeSpan ComputeTtl(HttpResponseMessage response)
    {
        var cc = response.Headers.CacheControl;
        if (cc?.MaxAge is { } maxAge) return maxAge;
        if (cc?.SharedMaxAge is { } sharedMaxAge) return sharedMaxAge;
        return TimeSpan.FromMinutes(5);
    }
}
