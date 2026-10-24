using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests.Utils;

/// <summary>图像字节探测。</summary>
public class ImageDataTests
{
    // 最小合法 PNG（1x1 透明像素）
    private static byte[] Png1x1 =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    // 最小 GIF（1x1）
    private static byte[] Gif1x1 =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00,
        0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44,
        0x01, 0x00, 0x3B
    ];

    // 最小 BMP（2x3）
    private static byte[] Bmp2x3 =>
    [
        0x42, 0x4D, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00,
        0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x00, 0x18, 0x00, 0x00
    ];

    [Fact]
    public void 认出PNG并读出尺寸()
    {
        var info = ImageData.Probe(Png1x1);
        Assert.Equal(ImageFormat.Png, info.Format);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
        Assert.True(info.HasSize);
    }

    [Fact]
    public void 认出GIF并读出尺寸()
    {
        var info = ImageData.Probe(Gif1x1);
        Assert.Equal(ImageFormat.Gif, info.Format);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
    }

    [Fact]
    public void 认出BMP并读出尺寸()
    {
        var info = ImageData.Probe(Bmp2x3);
        Assert.Equal(ImageFormat.Bmp, info.Format);
        Assert.Equal(2, info.Width);
        Assert.Equal(3, info.Height);
    }

    [Fact]
    public void 认出JPEG并读出尺寸()
    {
        // SOI + SOF0 段：宽 0x0040(64) 高 0x0020(32)
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            0x00, 0x20, 0x00, 0x40, 0x03,
            0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9
        ];
        var info = ImageData.Probe(jpeg);
        Assert.Equal(ImageFormat.Jpeg, info.Format);
        Assert.Equal(64, info.Width);
        Assert.Equal(32, info.Height);
    }

    [Fact]
    public void 认出静态WebP并读出尺寸()
    {
        // VP8X 容器：canvas 3x2，无动画标志
        byte[] webp = new byte[30];
        webp[0] = (byte)'R'; webp[1] = (byte)'I'; webp[2] = (byte)'F'; webp[3] = (byte)'F';
        webp[8] = (byte)'W'; webp[9] = (byte)'E'; webp[10] = (byte)'B'; webp[11] = (byte)'P';
        webp[12] = (byte)'V'; webp[13] = (byte)'P'; webp[14] = (byte)'8'; webp[15] = (byte)'X';
        webp[20] = 0x10; // flags：alpha，无动画
        webp[24] = 2;   // 宽 = 2 + 1 = 3
        webp[27] = 1;   // 高 = 1 + 1 = 2

        var info = ImageData.Probe(webp);
        Assert.Equal(ImageFormat.WebP, info.Format);
        Assert.Equal(3, info.Width);
        Assert.Equal(2, info.Height);
        Assert.False(info.IsAnimated);
    }

    [Fact]
    public void 认出动画WebP()
    {
        byte[] webp = new byte[30];
        webp[0] = (byte)'R'; webp[1] = (byte)'I'; webp[2] = (byte)'F'; webp[3] = (byte)'F';
        webp[8] = (byte)'W'; webp[9] = (byte)'E'; webp[10] = (byte)'B'; webp[11] = (byte)'P';
        webp[12] = (byte)'V'; webp[13] = (byte)'P'; webp[14] = (byte)'8'; webp[15] = (byte)'X';
        webp[20] = 0x12; // 动画标志置位
        webp[24] = 0; webp[27] = 0;

        var info = ImageData.Probe(webp);
        Assert.Equal(ImageFormat.WebP, info.Format);
        Assert.True(info.IsAnimated);
    }

    [Fact]
    public void 认出ICO()
    {
        byte[] ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00];
        Assert.Equal(ImageFormat.Ico, ImageData.Probe(ico).Format);
    }

    [Fact]
    public void 认出SVG()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"></svg>");
        Assert.Equal(ImageFormat.Svg, ImageData.Probe(svg).Format);
    }

    [Fact]
    public void 认不出HTML错误页()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>404</body></html>");
        var info = ImageData.Probe(html);
        Assert.Equal(ImageFormat.Unknown, info.Format);
        Assert.False(info.IsKnown);
    }

    [Fact]
    public void 提到svg字样的文本不是SVG()
    {
        // 判据必须是 "<svg" 标签本身，不能是任何含 "svg" 的文本 ——
        // 包管理器的 API 出错时经常回一段提到 svg 的 JSON。
        var json = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"unsupported svg format\"}");
        Assert.Equal(ImageFormat.Unknown, ImageData.Probe(json).Format);
    }

    [Fact]
    public void 认不出JSON响应()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
        Assert.Equal(ImageFormat.Unknown, ImageData.Probe(json).Format);
    }

    [Fact]
    public void 空数据不炸()
    {
        Assert.Equal(ImageFormat.Unknown, ImageData.Probe([]).Format);
        Assert.Equal(ImageFormat.Unknown, ImageData.Probe([0x01]).Format);
    }

    [Fact]
    public void 解base64裸文本()
    {
        var base64 = Convert.ToBase64String(Png1x1);
        Assert.True(ImageData.TryDecodeBase64(base64, out var data));
        Assert.Equal(Png1x1.Length, data.Length);
    }

    [Fact]
    public void 解dataURI()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png1x1);
        Assert.True(ImageData.TryDecodeBase64(uri, out var data));
        Assert.Equal(Png1x1.Length, data.Length);
    }

    [Fact]
    public void 解base64容忍换行()
    {
        var base64 = Convert.ToBase64String(Png1x1);
        // 注意：string.Join 的重载若给的是 IEnumerable<char[]>，
        // 会把每个字符之间都插上分隔符 —— 那是把字符拆开而不是折行。
        // 这里手动按长度切块。
        var parts = new List<string>();
        for (var i = 0; i < base64.Length; i += 10)
            parts.Add(base64.Substring(i, Math.Min(10, base64.Length - i)));
        var wrapped = string.Join("\n", parts);
        Assert.True(ImageData.TryDecodeBase64(wrapped, out var data));
        Assert.Equal(Png1x1.Length, data.Length);
    }

    [Fact]
    public void 解base64容忍缺少填充()
    {
        // 不少 API 返回的 base64 不带 '=' 填充（长度不是 4 的倍数）。
        // 解码器必须能自己补齐，而不是直接失败。
        var full = Convert.ToBase64String(Png1x1);
        var unpadded = full.TrimEnd('=');
        Assert.True(unpadded.Length % 4 != 0, "这个用例需要一段非 4 倍数长度的 base64");

        Assert.True(ImageData.TryDecodeBase64(unpadded, out var data));
        Assert.Equal(Png1x1.Length, data.Length);
    }

    [Fact]
    public void 非法base64返回失败而不是抛异常()
    {
        Assert.False(ImageData.TryDecodeBase64("这不是base64!!!", out _));
        Assert.False(ImageData.TryDecodeBase64(null, out _));
        Assert.False(ImageData.TryDecodeBase64("   ", out _));
        Assert.False(ImageData.TryDecodeBase64("data:image/png;base64", out _));
    }

    [Fact]
    public void 一步探测base64图像()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(Png1x1);
        Assert.True(ImageData.TryProbeBase64(uri, out var info));
        Assert.Equal(ImageFormat.Png, info.Format);
        Assert.Equal(1, info.Width);
    }

    [Fact]
    public void base64的HTML不是图像()
    {
        var html = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<html>404</html>"));
        Assert.False(ImageData.TryProbeBase64(html, out var info));
        Assert.Equal(ImageFormat.Unknown, info.Format);
    }
}
