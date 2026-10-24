// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Net/Http/HttpRequest.cs 与 PCL.Core/Utils/Exts/HttpRequestExtension.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.* → UMango.Core.Network.Http / UMango.Core.Utils。
//   2. 去掉原版里依赖 PCL 自身 NetworkService 的静态便捷方法
//      （GetStringAsync / GetJsonAsync / PostJsonAsync）——
//      那些方法在 PCL 里走的是 PCL 的 IoC 网络服务；UMango 有自己的 HttpService，
//      静态便捷方法只会制造第二个"网络从哪来"的真相来源。
//      需要一次性 GET/POST 的地方用 HttpRequestFactory + HttpSender。
//   3. 克隆方法保留：重试时必须用请求的副本，因为 HttpRequestMessage 不可重用。

using System.Net.Http;
using System.Text;

namespace UMango.Core.Network.Http;

/// <summary>请求消息工厂。</summary>
public static class HttpRequestFactory
{
    public static HttpRequestMessage Create(string url) => new(HttpMethod.Get, new Uri(url));
    public static HttpRequestMessage CreateHead(string url) => new(HttpMethod.Head, new Uri(url));
    public static HttpRequestMessage CreatePost(string url) => new(HttpMethod.Post, new Uri(url));
    public static HttpRequestMessage CreatePut(string url) => new(HttpMethod.Put, new Uri(url));
    public static HttpRequestMessage CreateDelete(string url) => new(HttpMethod.Delete, new Uri(url));
    public static HttpRequestMessage CreatePatch(string url) => new(HttpMethod.Patch, new Uri(url));
}

/// <summary>
/// 请求消息的克隆。
///
/// <see cref="HttpRequestMessage"/> **不可重用**：一次 SendAsync 之后它就被"消费"了
/// （尤其是带内容流的请求）。所以重试必须每次克隆一份新的。
/// </summary>
public static class HttpRequestCloning
{
    public static async Task<HttpRequestMessage> CloneAsync(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        if (request.Content is not null)
            clone.Content = await DeepCloneContentAsync(request.Content).ConfigureAwait(false);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            clone.Options.TryAdd(option.Key, option.Value);

        return clone;
    }

    /// <summary>
    /// 深度克隆内容：先把内容读进内存，再用 MemoryStream 建一个新的 StreamContent，
    /// 并把原内容头（Content-Type / Content-Length 等）复制过去。
    ///
    /// 读进内存是有代价的（大文件会占内存），但对"重试"这个场景是必要的：
    /// 流只能读一次，第二次尝试就没内容可发了。
    /// </summary>
    private static async Task<HttpContent> DeepCloneContentAsync(HttpContent content)
    {
        var ms = new MemoryStream();
        await content.CopyToAsync(ms).ConfigureAwait(false);
        ms.Position = 0;

        var clone = new StreamContent(ms);
        foreach (var header in content.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
