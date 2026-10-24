using System.Net;
using System.Text;
using System.Text.Json;
using UMango.Core.Utils;

namespace UMango.Core.Network.Http.Server;

/// <summary>
/// 服务端响应结构 —— 描述「要回给客户端什么」。
///
/// 对应 PCL-CE 的 <c>PCL.Core/IO/Net/Http/HttpRouteResponse.cs</c>（Apache-2.0）。
/// 逐字采用的部分：字段语义、<see cref="Pour"/> 的写入顺序、静态工厂的默认值。
///
/// 与原版的差异：
///   ① 字段改成属性 + 构造器（不再是 public 可变字段）—— 避免响应对象被外部改坏；
///   ② <see cref="Json"/> 不再依赖 PCL 的 <c>JsonCompat</c>，改用本仓库自己的 JSON 选项；
///   ③ 文本响应不再需要 PCL 的 <c>StringStream</c>（本仓库的 <c>StringStream</c>
///      在 <c>UMango.Core/Utils/StringStream.cs</c>，语义相同但独立实现）。
/// </summary>
public sealed class HttpRouteResponse
{
    /// <summary>构造。</summary>
    public HttpRouteResponse(
        HttpStatusCode? statusCode = null,
        string? contentType = null,
        Encoding? contentEncoding = null,
        string? redirectLocation = null,
        bool? sendChunked = null,
        CookieCollection? cookies = null,
        Stream? inputStream = null)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        ContentEncoding = contentEncoding;
        RedirectLocation = redirectLocation;
        SendChunked = sendChunked;
        Cookies = cookies;
        InputStream = inputStream;
    }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode? StatusCode { get; set; }

    /// <summary>内容 MIME 类型。</summary>
    public string? ContentType { get; set; }

    /// <summary>内容的字符编码。</summary>
    public Encoding? ContentEncoding { get; set; }

    /// <summary>重定向目标 URL 或路径。</summary>
    public string? RedirectLocation { get; set; }

    /// <summary>是否使用分块传输编码。</summary>
    public bool? SendChunked { get; set; }

    /// <summary>随响应添加的 Cookie。</summary>
    public CookieCollection? Cookies { get; set; }

    /// <summary>响应内容流（非空时会直接拷进实际响应）。</summary>
    public Stream? InputStream { get; set; }

    /// <summary>把这份响应写进 <see cref="HttpListenerResponse"/>。</summary>
    public void Pour(HttpListenerResponse target)
    {
        target.StatusCode = (int)(StatusCode ?? HttpStatusCode.OK);
        if (ContentType is { } contentType) target.ContentType = contentType;
        if (ContentEncoding is { } contentEncoding) target.ContentEncoding = contentEncoding;
        if (RedirectLocation is { } redirectLocation) target.RedirectLocation = redirectLocation;
        if (SendChunked is { } sendChunked) target.SendChunked = sendChunked;
        if (Cookies is { } cookies) target.Cookies = cookies;
        if (InputStream is { } inputStream) inputStream.CopyTo(target.OutputStream);
    }

    /// <summary>指定状态码的空响应。</summary>
    public static HttpRouteResponse Empty(HttpStatusCode statusCode) => new(statusCode: statusCode);

    /// <summary>204 No Content。</summary>
    public static HttpRouteResponse NoContent => Empty(HttpStatusCode.NoContent);

    /// <summary>400 Bad Request。</summary>
    public static HttpRouteResponse BadRequest => Empty(HttpStatusCode.BadRequest);

    /// <summary>403 Forbidden。</summary>
    public static HttpRouteResponse Forbidden => Empty(HttpStatusCode.Forbidden);

    /// <summary>404 Not Found。</summary>
    public static HttpRouteResponse NotFound => Empty(HttpStatusCode.NotFound);

    /// <summary>500 Internal Server Error。</summary>
    public static HttpRouteResponse InternalServerError => Empty(HttpStatusCode.InternalServerError);

    /// <summary>502 Bad Gateway。</summary>
    public static HttpRouteResponse BadGateway => Empty(HttpStatusCode.BadGateway);

    /// <summary>响应一个流的内容。</summary>
    public static HttpRouteResponse Input(
        Stream stream,
        string contentType = "application/octet-stream",
        Encoding? encoding = null)
        => new(contentType: contentType, contentEncoding: encoding ?? Encoding.UTF8, inputStream: stream);

    /// <summary>响应一段文本。</summary>
    public static HttpRouteResponse Text(
        string text,
        string contentType = "text/plain",
        Encoding? encoding = null)
        => Input(new StringStream(text, encoding ?? Encoding.UTF8), contentType, encoding ?? Encoding.UTF8);

    /// <summary>响应一个重定向。</summary>
    public static HttpRouteResponse Redirect(
        string location,
        HttpStatusCode statusCode = HttpStatusCode.Found)
        => new(statusCode: statusCode, redirectLocation: location);

    /// <summary>响应一个 JSON 对象（固定 UTF-8）。</summary>
    public static HttpRouteResponse Json(object obj, JsonSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, obj, options ?? JsonCompat.SerializerOptions);
        stream.Position = 0;
        return new HttpRouteResponse
        {
            ContentEncoding = Encoding.UTF8,
            // 注意：不能写成 "application/json, charset=utf-8" ——
            // HttpListener 会把带逗号的 ContentType 解析失败，
            // 结果响应里**根本没有 Content-Type 头**（客户端拿到 null）。
            // 字符集由 ContentEncoding 单独声明。
            ContentType = "application/json",
            InputStream = stream,
        };
    }
}
