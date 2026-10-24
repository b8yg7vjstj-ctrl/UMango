// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/IO/Net/Http/HttpContentExtension.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.IO.Net.Http → UMango.Core.Network.Http。
//   2. C# 14 的 extension 成员语法改为传统扩展方法（等价，且不依赖新语法的运行时支持）。
//   3. JSON 序列化改用本仓库的 JsonCompat.SerializerOptions（原版也用 JsonCompat，语义一致）。
//   4. 保留原版"内容头与请求头分开加"的行为 —— Content-* 头必须加在 HttpContent 上，
//      否则 HttpClient 会在发送时报 InvalidOperationException。

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UMango.Core.Utils;

namespace UMango.Core.Network.Http;

/// <summary>
/// 请求内容与请求头的组装。
///
/// 这些是"一次性"装饰器：调用后返回同一个 <see cref="HttpRequestMessage"/>，
/// 方便链式书写。
/// </summary>
public static class HttpRequestContentExtensions
{
    // ── 请求头 ────────────────────────────────────────────

    /// <summary>
    /// 加一个请求头。<c>Content-</c> 开头的头会加到内容上而不是请求上 ——
    /// 这是 .NET 的硬性要求，放错地方会直接抛异常。
    /// </summary>
    public static HttpRequestMessage WithHeader(this HttpRequestMessage request, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
        {
            // 必须先 Remove 再 Add：TryAddWithoutValidation 在同名头已存在时
            // 会**静默失败**（返回 false 且不抛），于是"设置 Content-Type"
            // 看起来成功了，实际上头还是原来的那个。
            // PCL 原版就有这个缺陷 —— 它先 WithContent 再 WithHeader 时，
            // StringContent 已经带了 Content-Type，后加的那个根本没生效。
            request.Content.Headers.Remove(key);
            request.Content.Headers.TryAddWithoutValidation(key, value);
        }
        else
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
        return request;
    }

    public static HttpRequestMessage WithHeaders(this HttpRequestMessage request, IDictionary<string, string> pairs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pairs);
        foreach (var item in pairs) request.WithHeader(item.Key, item.Value);
        return request;
    }

    public static HttpRequestMessage WithHeader(this HttpRequestMessage request, KeyValuePair<string, string> pair)
        => request.WithHeader(pair.Key, pair.Value);

    /// <summary>设置认证头（scheme + token）。</summary>
    public static HttpRequestMessage WithAuthentication(this HttpRequestMessage request, string scheme, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentException.ThrowIfNullOrEmpty(token);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        return request;
    }

    public static HttpRequestMessage WithBearerToken(this HttpRequestMessage request, string token)
        => request.WithAuthentication("Bearer", token);

    // ── 内容 ──────────────────────────────────────────────

    public static HttpRequestMessage WithContent(this HttpRequestMessage request, HttpContent content, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Content = content;
        if (contentType is not null) request.WithHeader("Content-Type", contentType);
        return request;
    }

    public static HttpRequestMessage WithContent(this HttpRequestMessage request, string content, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Content = contentType is null
            ? new StringContent(content, Encoding.UTF8)
            : new StringContent(content, Encoding.UTF8, contentType);
        return request;
    }

    public static HttpRequestMessage WithBinaryContent(this HttpRequestMessage request, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Content = new ByteArrayContent(content);
        return request;
    }

    /// <summary>把对象序列化成 JSON 内容。</summary>
    public static HttpRequestMessage WithJsonContent<T>(this HttpRequestMessage request, T content,
        string? contentType = null, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(content, options ?? JsonCompat.SerializerOptions),
            Encoding.UTF8,
            contentType ?? "application/json");
        return request;
    }

    /// <summary>表单内容（application/x-www-form-urlencoded）。</summary>
    public static HttpRequestMessage WithFormContent(this HttpRequestMessage request, string form)
        => request.WithContent(new ByteArrayContent(Encoding.UTF8.GetBytes(form)), "application/x-www-form-urlencoded");

    public static HttpRequestMessage WithFormContent(this HttpRequestMessage request,
        IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Content = new FormUrlEncodedContent(pairs);
        return request;
    }

    // ── Cookie ────────────────────────────────────────────

    /// <summary>
    /// 加一个 Cookie。已有的同名 Cookie 头会被合并（追加 "; "）。
    /// 值里的非法字符会被转义 —— 否则一个带分号的值就能伪造出额外的 Cookie。
    /// </summary>
    public static HttpRequestMessage WithCookie(this HttpRequestMessage request, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(value);

        var newCookie = $"{name}={EscapeCookieValue(value)}";

        if (request.Headers.TryGetValues("Cookie", out var existingValues))
        {
            var existing = string.Join("; ", existingValues);
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", $"{existing}; {newCookie}");
        }
        else
        {
            request.Headers.Add("Cookie", newCookie);
        }
        return request;
    }

    /// <summary>Cookie 值里不允许出现的字符。</summary>
    private static readonly char[] ForbiddenCookieValueChars =
        [';', ',', ' ', '\r', '\n', '\t', '\0', '=', '"', '\'', '\\', '<', '>'];

    internal static string EscapeCookieValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var needsEncoding = value.Any(c => ForbiddenCookieValueChars.Contains(c) || char.IsControl(c));
        return needsEncoding ? Uri.EscapeDataString(value) : value;
    }
}
