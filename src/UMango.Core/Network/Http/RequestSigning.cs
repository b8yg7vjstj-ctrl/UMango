// SPDX-License-Identifier: Apache-2.0
//
// 借鉴自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   Plain Craft Launcher 2/Modules/Network/Http/RequestSigning.cs
//
// 原文件位于**自定义许可目录**（不是 Apache-2.0），因此**不逐字复制**，
// 只借鉴其**机制**，在本文件重新实现（思路一致、代码全部自写）。
//
// 原版做的两件事：
//   1. SecretCdnSign：把 URL 里的 "{CDN}" 标记展开成实际 CDN 地址（并把空格转 %20）。
//   2. SecretHeadersSign：给请求加上 UA / Referer，并对特定域名加 API Key 头。
//
// UMango 的差异与理由：
//   1. 原版把 CurseForge 的 API Key 写死在 PCL 官方服务配置里 —— UMango 不该向
//      任何一家第三方服务表明身份，也不该内置别人的密钥。改为：**签名器可插拔**，
//      密钥由调用方注入；内置实现只有通用的 HMAC 签名。
//   2. 原版强制 `HttpVersion.Version20` + `RequestVersionOrLower`。这是针对
//      特定 CDN 的兼容 hack，会让**所有**请求降级到 HTTP/1.1。既然是通用层，
//      就把版本策略交给调用方，默认沿用运行时默认。
//   3. 原版用 `client.Headers.Add(...)` —— 若请求已带同名头会**抛异常**
//      （"Cannot add value because header 'User-Agent' does not support multiple values"）。
//      改为幂等的 TryAddWithoutValidation 语义。
//   4. 原版 `SecretCdnSign` 用 `EndsWithF("{CDN}")` —— 标记**只在 URL 结尾**时才展开。
//      这意味着 `https://cdn/{CDN}/file.zip` 这种中间带标记的 URL 不会被处理，
//      标记会原样发给服务器。UMango 改为**任意位置**都展开。

using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace UMango.Core.Network.Http;

/// <summary>
/// 一次签名的结果：往请求上追加的头。
/// </summary>
public sealed class RequestSigningResult
{
    /// <summary>要追加的头（名字已按 HTTP 规范规范化）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public RequestSigningResult(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Headers = headers;
    }

    /// <summary>无附加头的空结果。</summary>
    public static RequestSigningResult Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// 请求签名器。
///
/// 用途：为发往**包源镜像 / 自建源**的请求补上鉴权头，让对端能验证"这个请求确实来自本客户端"。
/// </summary>
public interface IRequestSigner
{
    /// <summary>
    /// 为请求计算签名头。
    /// </summary>
    /// <param name="request">待签名的请求（只读，不应修改其内容）。</param>
    /// <param name="uri">请求的目标地址。</param>
    /// <param name="ct">取消令牌。</param>
    Task<RequestSigningResult> SignAsync(HttpRequestMessage request, Uri uri, CancellationToken ct = default);
}

/// <summary>
/// 通用 HMAC-SHA256 请求签名器。
///
/// 签名串：<c>{method}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{sha256(body)}</c>
/// 头：<c>X-UMango-Timestamp</c> / <c>X-UMango-Nonce</c> / <c>X-UMango-Signature</c>。
///
/// 这套头名是 UMango 自定的，不与任何第三方服务的私有协议冲突。
/// </summary>
public sealed class HmacRequestSigner : IRequestSigner
{
    private readonly byte[] _key;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _nonceFactory;

    /// <summary>构造。</summary>
    /// <param name="key">HMAC 密钥（不能为空）。</param>
    /// <param name="clock">时钟（测试可注入固定时钟）。</param>
    /// <param name="nonceFactory">随机串工厂（测试可注入）。</param>
    public HmacRequestSigner(
        byte[] key,
        Func<DateTimeOffset>? clock = null,
        Func<string>? nonceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0) throw new ArgumentException("密钥不能为空。", nameof(key));

        _key = key;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nonceFactory = nonceFactory ?? (() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
    }

    /// <summary>时间戳头名。</summary>
    public const string TimestampHeader = "X-UMango-Timestamp";

    /// <summary>随机串头名。</summary>
    public const string NonceHeader = "X-UMango-Nonce";

    /// <summary>签名头名。</summary>
    public const string SignatureHeader = "X-UMango-Signature";

    /// <summary>计算签名串（公开出来便于测试与对端复现）。</summary>
    public string BuildSignatureString(HttpMethod method, string pathAndQuery, long timestamp, string nonce, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(body);

        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return string.Join('\n',
            method.Method.ToUpperInvariant(),
            pathAndQuery,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            bodyHash);
    }

    /// <inheritdoc />
    public async Task<RequestSigningResult> SignAsync(HttpRequestMessage request, Uri uri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(uri);
        ct.ThrowIfCancellationRequested();

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var timestamp = _clock().ToUnixTimeSeconds();
        var nonce = _nonceFactory();
        var pathAndQuery = uri.PathAndQuery;

        var signatureString = BuildSignatureString(request.Method, pathAndQuery, timestamp, nonce, body);
        using var hmac = new HMACSHA256(_key);
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureString))).ToLowerInvariant();

        return new RequestSigningResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TimestampHeader] = timestamp.ToString(CultureInfo.InvariantCulture),
            [NonceHeader] = nonce,
            [SignatureHeader] = signature,
        });
    }
}

/// <summary>
/// 静态请求签名工具：CDN 标记展开 + 头注入。
/// </summary>
public static class RequestSigning
{
    /// <summary>
    /// 展开 URL 里的 <c>{CDN}</c> 标记 —— 标记代表**主机名**。
    ///
    /// 原版只在 URL **结尾**是 <c>{CDN}</c> 时才处理（<c>EndsWithF</c>），
    /// 而且它是把标记**删掉**（<c>Replace("{CDN}", "")</c>），并不填入任何地址 ——
    /// 也就是说原版的 "{CDN}" 只是个"这个 URL 来自 CDN"的**标记**，不是占位符。
    /// 中间带标记的 URL 在原版里不会被处理，标记会原样发给对端。
    ///
    /// UMango 的语义：把 <c>{CDN}</c> 当作**主机名占位符**，
    /// 用 <paramref name="cdnHost"/> 替换（例：<c>https://{CDN}/pkg.tgz</c> +
    /// <c>cdn.example.com</c> → <c>https://cdn.example.com/pkg.tgz</c>）。
    /// 任何位置的标记都展开。
    /// </summary>
    public static string ExpandCdnMarker(string urlWithMark, string cdnHost)
    {
        ArgumentNullException.ThrowIfNull(urlWithMark);

        if (!urlWithMark.Contains("{CDN}", StringComparison.Ordinal))
            return urlWithMark;

        var expanded = urlWithMark.Replace("{CDN}", cdnHost ?? string.Empty, StringComparison.Ordinal);
        return EscapeSpaces(expanded);
    }

    /// <summary>把 URL 里的空白字符转成百分号编码（空格 → <c>%20</c>）。</summary>
    public static string EscapeSpaces(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var sb = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (char.IsWhiteSpace(c))
                sb.Append("%20");
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 给请求补上默认头：User-Agent（可选 Referer）。
    ///
    /// 与原版的差异：
    ///   · 原版强制 HTTP/2 + <c>RequestVersionOrLower</c>（针对特定 CDN 的兼容 hack，
    ///     会让所有请求降级）；这里不动版本策略。
    ///   · 原版用 <c>Headers.Add</c> —— 请求已带同名头时**会抛异常**。
    ///     这里用 <c>TryAddWithoutValidation</c>，幂等且不校验值格式。
    ///   · 原版只对 CurseForge 三个域名加 API Key；这里**不加任何厂商专有头** ——
    ///     厂商鉴权交给可插拔的 <see cref="IRequestSigner"/>。
    /// </summary>
    public static void ApplyDefaultHeaders(
        HttpRequestMessage request,
        string? userAgent = null,
        string? referer = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 先清掉旧值再加 —— TryAddWithoutValidation 允许**多值**，
        // 重复调用会攒出好几个 User-Agent，对端看到的是 "UMango/1.0, UMango/1.0"。
        // 原版用 Headers.Add 反而会直接抛异常；这里做成幂等覆盖。
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.Remove("Referer");
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }
    }

    /// <summary>
    /// 用签名器补上签名头（不覆盖请求已有的同名头）。
    /// </summary>
    public static async Task ApplySignatureAsync(
        HttpRequestMessage request,
        Uri uri,
        IRequestSigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(signer);

        var result = await signer.SignAsync(request, uri, ct).ConfigureAwait(false);
        foreach (var (name, value) in result.Headers)
        {
            // 同理：签名头也要幂等覆盖，否则重复签名会攒出多份。
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
