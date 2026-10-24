// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/IO/Net/Http/HttpResponseExtension.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.IO.Net.Http → UMango.Core.Network.Http。
//   2. C# 14 的 extension 成员语法改为传统扩展方法。
//   3. 取消 Async 变体的同步版本（AsString() 同步阻塞版）——
//      同步等待异步在 WPF 的 UI 线程上会死锁，本仓库一律异步。
//   4. JsonCompat 换成同名的本仓库实现（语义一致）。
//   5. 补了一点：GetHeader/GetContentHeaders 在 Content 为 null 时不能 NRE
//      （原版 GetContentHeaders 直接访问 responseMessage.Content.Headers，
//       而 204/304 响应下 Content 可能为 null）。

using System.IO;
using System.Net.Http;
using System.Text.Json;
using UMango.Core.Utils;

namespace UMango.Core.Network.Http;

/// <summary>响应体的读取方式。</summary>
public static class HttpResponseExtensions
{
    public static bool IsSuccess(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// 读响应体为字符串。取消/超时会被翻译成 <see cref="TimeoutException"/> ——
    /// 调用方据此可以区分"服务端慢"与"我主动取消"。
    /// </summary>
    public static async Task<string> AsStringAsync(this HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("The request was canceled due to a timeout.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The operation was canceled.");
        }
    }

    public static async Task<Stream> AsStreamAsync(this HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("The request was canceled due to a timeout.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The operation was canceled.");
        }
    }

    public static async Task<byte[]> AsByteArrayAsync(this HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            throw new TimeoutException("The request was canceled due to a timeout.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The operation was canceled.");
        }
    }

    /// <summary>
    /// 读响应体为 JSON。JSON 语法错误会被翻译成 <see cref="InvalidDataException"/> ——
    /// 上游返回了一堆 HTML 错误页时，调用方据此能看出"这不是 JSON"而不是"反序列化失败"。
    /// </summary>
    public static async Task<T?> AsJsonAsync<T>(this HttpResponseMessage response,
        JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            await using var stream = await response.AsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, options ?? JsonCompat.SerializerOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Failed to deserialize JSON response.", ex);
        }
    }

    /// <summary>合并响应头与内容头。</summary>
    public static Dictionary<string, string[]> GetHeaders(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in response.Headers) dict[kvp.Key] = kvp.Value.ToArray();
        if (response.Content is not null)
            foreach (var kvp in response.Content.Headers) dict[kvp.Key] = kvp.Value.ToArray();
        return dict;
    }

    public static Dictionary<string, string[]> GetContentHeaders(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content is null) return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        return response.Content.Headers.ToDictionary(k => k.Key, v => v.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public static string[] GetHeader(this HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.TryGetValues(name, out var values) ||
            (response.Content is not null && response.Content.Headers.TryGetValues(name, out values)))
            return values.ToArray();
        return [];
    }

    public static bool TryGetHeader(this HttpResponseMessage response, string name, out string[] values)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.TryGetValues(name, out var headerValues) ||
            (response.Content is not null && response.Content.Headers.TryGetValues(name, out headerValues)))
        {
            values = headerValues.ToArray();
            return true;
        }
        values = [];
        return false;
    }

    public static string? GetFirstHeaderValue(this HttpResponseMessage response, string name)
        => response.GetHeader(name).FirstOrDefault();

    public static bool TryGetFirstHeaderValue(this HttpResponseMessage response, string name, out string value)
    {
        var values = response.GetHeader(name);
        if (values.Length == 0)
        {
            value = string.Empty;
            return false;
        }
        value = values[0];
        return true;
    }

    /// <summary>非 2xx 就抛 <see cref="HttpRequestException"/>（不含响应体）。</summary>
    public static void EnsureSuccessStatusCode(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP request failed with status code {response.StatusCode}: {response.ReasonPhrase}");
    }

    /// <summary>非 2xx 就抛 <see cref="HttpRequestException"/>，异常消息里带上响应体。</summary>
    public static async Task EnsureSuccessStatusCodeWithContentAsync(this HttpResponseMessage response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.AsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP request failed with status code {response.StatusCode}: {response.ReasonPhrase}. Response content: {content}");
        }
    }
}
