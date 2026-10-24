using System.Net.Http;
using System.Text;
using UMango.Core.Network.Http;
using Xunit;

namespace UMango.Core.Tests.Network.Http;

/// <summary>
/// 请求签名层测试。
///
/// 对应 PCL-CE 的 <c>Modules/Network/Http/RequestSigning.cs</c>（受限许可目录，
/// UMango 重新实现）。这里守的是**机制**与**修掉的原版缺陷**。
/// </summary>
public class RequestSigningTests
{
    // ---------- CDN 标记展开 ----------

    [Fact]
    public void 没有标记时原样返回()
    {
        Assert.Equal("https://example.com/a.zip", RequestSigning.ExpandCdnMarker("https://example.com/a.zip", "https://cdn"));
    }

    [Fact]
    public void 标记在结尾时展开()
    {
        Assert.Equal("https://example.com/a.zipcdn.example.com",
            RequestSigning.ExpandCdnMarker("https://example.com/a.zip{CDN}", "cdn.example.com"));
    }

    /// <summary>
    /// 原版用 <c>EndsWithF("{CDN}")</c> —— 只有标记在**结尾**时才展开。
    /// 中间带标记的 URL 会被原样发出去，对端收到的是字面量 "{CDN}"。
    /// </summary>
    [Fact]
    public void 标记在中间也展开()
    {
        Assert.Equal("https://cdn.example.com/path/a.zip",
            RequestSigning.ExpandCdnMarker("https://{CDN}/path/a.zip", "cdn.example.com"));
    }

    [Fact]
    public void 多个标记都展开()
    {
        Assert.Equal("https://c/a/c/b",
            RequestSigning.ExpandCdnMarker("https://{CDN}/a/{CDN}/b", "c"));
    }

    [Fact]
    public void 展开后空格转百分号编码()
    {
        Assert.Equal("https://cdn.example.com/a%20b.zip",
            RequestSigning.ExpandCdnMarker("https://{CDN}/a b.zip", "cdn.example.com"));
    }

    [Fact]
    public void 制表符与换行也转百分号编码()
    {
        Assert.Equal("a%20b%20c", RequestSigning.EscapeSpaces("a\tb\nc"));
    }

    // ---------- 默认头 ----------

    [Fact]
    public void 应用默认头会加上UA和Referer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0", "https://example.com/");

        Assert.Equal("UMango/1.0", request.Headers.GetValues("User-Agent").Single());
        Assert.Equal("https://example.com/", request.Headers.GetValues("Referer").Single());
    }

    [Fact]
    public void 空白UA与Referer不加头()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        RequestSigning.ApplyDefaultHeaders(request, "  ", null);

        Assert.False(request.Headers.Contains("User-Agent"));
        Assert.False(request.Headers.Contains("Referer"));
    }

    /// <summary>
    /// 原版用 <c>client.Headers.Add("User-Agent", ua)</c>。
    /// 请求若已带 UA，<c>Add</c> 会**抛异常**（某些头不支持多值）。
    /// UMango 改为幂等的 TryAddWithoutValidation。
    /// </summary>
    [Fact]
    public void 重复应用默认头不会抛异常()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0");
        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0");
        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0");

        Assert.Equal("UMango/1.0", request.Headers.GetValues("User-Agent").Single());
    }

    [Fact]
    public void 重复应用默认头只保留一份UA()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0", "https://example.com/");
        RequestSigning.ApplyDefaultHeaders(request, "UMango/2.0", "https://example.com/x");

        Assert.Equal("UMango/2.0", request.Headers.GetValues("User-Agent").Single());
        Assert.Equal("https://example.com/x", request.Headers.GetValues("Referer").Single());
    }

    [Fact]
    public void 应用默认头不动HTTP版本()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        var before = request.Version;
        RequestSigning.ApplyDefaultHeaders(request, "UMango/1.0");
        Assert.Equal(before, request.Version);
    }

    // ---------- HMAC 签名 ----------

    [Fact]
    public void 签名器拒绝空密钥()
    {
        Assert.Throws<ArgumentException>(() => new HmacRequestSigner([]));
        Assert.Throws<ArgumentNullException>(() => new HmacRequestSigner(null!));
    }

    [Fact]
    public async Task 签名结果包含三个头()
    {
        var signer = new HmacRequestSigner(
            "secret-key"u8.ToArray(),
            () => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            () => "fixed-nonce");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a?b=1");
        var result = await signer.SignAsync(request, new Uri("https://example.com/a?b=1"));

        Assert.Equal(3, result.Headers.Count);
        Assert.Equal("1700000000", result.Headers[HmacRequestSigner.TimestampHeader]);
        Assert.Equal("fixed-nonce", result.Headers[HmacRequestSigner.NonceHeader]);
        Assert.Equal(64, result.Headers[HmacRequestSigner.SignatureHeader].Length);
    }

    [Fact]
    public async Task 同一输入签名稳定()
    {
        var signer = new HmacRequestSigner(
            "k"u8.ToArray(),
            () => DateTimeOffset.FromUnixTimeSeconds(100),
            () => "n");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/x");
        var a = await signer.SignAsync(request, new Uri("https://example.com/x"));
        var b = await signer.SignAsync(request, new Uri("https://example.com/x"));

        Assert.Equal(a.Headers[HmacRequestSigner.SignatureHeader], b.Headers[HmacRequestSigner.SignatureHeader]);
    }

    [Fact]
    public async Task 路径不同签名不同()
    {
        var signer = new HmacRequestSigner("k"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(1), () => "n");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a");
        var a = await signer.SignAsync(request, new Uri("https://example.com/a"));
        var b = await signer.SignAsync(request, new Uri("https://example.com/b"));

        Assert.NotEqual(a.Headers[HmacRequestSigner.SignatureHeader], b.Headers[HmacRequestSigner.SignatureHeader]);
    }

    [Fact]
    public async Task 方法不同签名不同()
    {
        var signer = new HmacRequestSigner("k"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(1), () => "n");
        var uri = new Uri("https://example.com/a");

        using var get = new HttpRequestMessage(HttpMethod.Get, uri);
        using var post = new HttpRequestMessage(HttpMethod.Post, uri);

        var a = await signer.SignAsync(get, uri);
        var b = await signer.SignAsync(post, uri);

        Assert.NotEqual(a.Headers[HmacRequestSigner.SignatureHeader], b.Headers[HmacRequestSigner.SignatureHeader]);
    }

    [Fact]
    public async Task 密钥不同签名不同()
    {
        var uri = new Uri("https://example.com/a");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var a = await new HmacRequestSigner("k1"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(1), () => "n")
            .SignAsync(request, uri);
        var b = await new HmacRequestSigner("k2"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(1), () => "n")
            .SignAsync(request, uri);

        Assert.NotEqual(a.Headers[HmacRequestSigner.SignatureHeader], b.Headers[HmacRequestSigner.SignatureHeader]);
    }

    [Fact]
    public async Task 请求体参与签名()
    {
        var uri = new Uri("https://example.com/a");
        var signer = new HmacRequestSigner("k"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(1), () => "n");

        using var empty = new HttpRequestMessage(HttpMethod.Post, uri);
        using var filled = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
        };

        var a = await signer.SignAsync(empty, uri);
        var b = await signer.SignAsync(filled, uri);

        Assert.NotEqual(a.Headers[HmacRequestSigner.SignatureHeader], b.Headers[HmacRequestSigner.SignatureHeader]);
    }

    [Fact]
    public void 签名串格式固定()
    {
        var signer = new HmacRequestSigner("k"u8.ToArray());
        var s = signer.BuildSignatureString(HttpMethod.Get, "/a?b=1", 42, "n", []);
        var lines = s.Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.Equal("GET", lines[0]);
        Assert.Equal("/a?b=1", lines[1]);
        Assert.Equal("42", lines[2]);
        Assert.Equal("n", lines[3]);
        Assert.Equal(64, lines[4].Length);
    }

    [Fact]
    public async Task 应用签名把头写进请求()
    {
        var signer = new HmacRequestSigner(
            "k"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(7), () => "n");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a");
        await RequestSigning.ApplySignatureAsync(request, new Uri("https://example.com/a"), signer);

        Assert.Equal("7", request.Headers.GetValues(HmacRequestSigner.TimestampHeader).Single());
        Assert.Equal("n", request.Headers.GetValues(HmacRequestSigner.NonceHeader).Single());
        Assert.True(request.Headers.Contains(HmacRequestSigner.SignatureHeader));
    }

    /// <summary>
    /// 签名头是**本层自己算出来的**，重复签名必须覆盖旧值 ——
    /// 否则会攒出多份 nonce/timestamp，对端按最后一份算就与签名不匹配。
    /// </summary>
    [Fact]
    public async Task 应用签名覆盖已有同名头()
    {
        var signer = new HmacRequestSigner("k"u8.ToArray(), () => DateTimeOffset.FromUnixTimeSeconds(7), () => "n");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a");
        request.Headers.TryAddWithoutValidation(HmacRequestSigner.NonceHeader, "preset");

        await RequestSigning.ApplySignatureAsync(request, new Uri("https://example.com/a"), signer);

        Assert.Equal("n", request.Headers.GetValues(HmacRequestSigner.NonceHeader).Single());
    }

    [Fact]
    public async Task 空结果表示无附加头()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a");
        await RequestSigning.ApplySignatureAsync(request, new Uri("https://example.com/a"), new NullSigner());

        Assert.False(request.Headers.Contains(HmacRequestSigner.SignatureHeader));
    }

    private sealed class NullSigner : IRequestSigner
    {
        public Task<RequestSigningResult> SignAsync(HttpRequestMessage request, Uri uri, CancellationToken ct = default)
            => Task.FromResult(RequestSigningResult.Empty);
    }
}
