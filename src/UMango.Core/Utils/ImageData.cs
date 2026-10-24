// SPDX-License-Identifier: Apache-2.0
//
// 本文件的能力对应 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/ImageLoaderHelper.cs 与 PCL.Core/UI/Media/ImageConverter.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 原版两个文件都直接依赖 WPF（WIC 解码 + Image 控件），
//      UMango 改为只做**与平台无关的字节级探测**：识别格式、读出内在尺寸、
//      校验 base64 数据。真正解码仍交给系统（WPF 侧用 WIC）。
//      理由：Core 的 net10.0 支必须能在 macOS 上编译并被单元测试覆盖，
//      而 WIC 只存在于 Windows。
//   2. 原版 ImageLoaderHelper 把「base64 → Image.Source」和「失败回落默认图」
//      写在一起；UMango 把「判断这个 base64 是不是能显示的图」独立出来，
//      让「要不要显示这个图标」成为调用方的选择，而不是加载器替它决定。
//   3. 原版 ImageConverter 的 WebP→PNG 转换依赖 WIC 编解码器，
//      移动端/非 Windows 上没有对应能力；UMango 改为**探测** WebP 并读出尺寸，
//      转换交给具备解码能力的宿主。

using System.Buffers.Binary;

namespace UMango.Core.Utils;

/// <summary>图像格式。</summary>
public enum ImageFormat
{
    Unknown,
    Png,
    Jpeg,
    Gif,
    Bmp,
    WebP,
    Ico,
    Svg,
}

/// <summary>图像的探测结果。</summary>
/// <param name="Format">识别出的格式。</param>
/// <param name="Width">内在宽度（像素）；未知格式为 0。</param>
/// <param name="Height">内在高度（像素）；未知格式为 0。</param>
/// <param name="IsAnimated">是否为动图（GIF / 动态 WebP）。</param>
public readonly record struct ImageInfo(ImageFormat Format, int Width, int Height, bool IsAnimated)
{
    public bool IsKnown => Format != ImageFormat.Unknown;

    public bool HasSize => Width > 0 && Height > 0;
}

/// <summary>
/// 图像字节探测：只读文件头，不解码像素。
///
/// 包管理器的搜索结果常常带 base64 图标（或远程图片 URL）。在把图交给
/// 系统解码器之前，先用这里判断「这是不是一张能显示的图、有多大」：
///   · 不能显示的（HTML 错误页、JSON 响应）就别占位；
///   · 知道尺寸就能先占好位，避免图片加载后布局跳动。
/// </summary>
public static class ImageData
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>探测图像字节。</summary>
    /// <param name="data">图像数据；为空或太短返回 <see cref="ImageFormat.Unknown"/>。</param>
    public static ImageInfo Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return default;

        if (StartsWith(data, PngSignature))
            return ProbePng(data);

        if (data[0] == 0xFF && data[1] == 0xD8)
            return ProbeJpeg(data);

        if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
            return ProbeGif(data);

        if (data[0] == 'B' && data[1] == 'M')
            return ProbeBmp(data);

        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            return ProbeWebp(data);

        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
            return new ImageInfo(ImageFormat.Ico, 0, 0, false);

        // SVG：文本内容（可能有 BOM / 前导空白）。
        var text = AsText(data);
        if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            return new ImageInfo(ImageFormat.Svg, 0, 0, false);

        return default;
    }

    private static ImageInfo ProbePng(ReadOnlySpan<byte> data)
    {
        // IHDR 宽高在字节 16..23（big-endian）。
        if (data.Length < 24)
            return new ImageInfo(ImageFormat.Png, 0, 0, false);

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        return new ImageInfo(ImageFormat.Png, width, height, IsAnimatedPng(data));
    }

    /// <summary>PNG 动图靠 acTL 块判断。</summary>
    private static bool IsAnimatedPng(ReadOnlySpan<byte> data)
    {
        // acTL 出现在 IDAT 之前的块序列里。完整扫描代价高，
        // 这里只查前若干个块里的 "acTL" 标记 —— 够用且不误判静态图。
        var limit = Math.Min(data.Length, 4096);
        for (var i = 8; i + 4 <= limit; i++)
        {
            if (data[i] == 'a' && data[i + 1] == 'c' && data[i + 2] == 'T' && data[i + 3] == 'L')
                return true;
        }

        return false;
    }

    private static ImageInfo ProbeJpeg(ReadOnlySpan<byte> data)
    {
        // 遍历 JPEG 段，SOF0..SOF15（跳过 C4/C8/CC）里带宽高。
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (i + 3 >= data.Length)
                break;

            var length = (data[i + 2] << 8) | data[i + 3];
            var isSof = marker >= 0xC0 && marker <= 0xCF
                       && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                if (i + 9 > data.Length)
                    break;
                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];
                return new ImageInfo(ImageFormat.Jpeg, width, height, false);
            }

            if (length <= 0)
                break;
            i += 2 + length;
        }

        return new ImageInfo(ImageFormat.Jpeg, 0, 0, false);
    }

    private static ImageInfo ProbeGif(ReadOnlySpan<byte> data)
    {
        // 逻辑屏描述符：宽高在字节 6..9（little-endian）。
        if (data.Length < 10)
            return new ImageInfo(ImageFormat.Gif, 0, 0, false);

        var width = data[6] | (data[7] << 8);
        var height = data[8] | (data[9] << 8);

        // 动图：出现 NETSCAPE2.0 扩展块。
        var isAnimated = false;
        for (var i = 10; i + 11 < Math.Min(data.Length, 1024); i++)
        {
            if (data[i] == 'N' && data[i + 1] == 'E' && data[i + 2] == 'T'
                && data[i + 3] == 'S' && data[i + 4] == 'C' && data[i + 5] == 'A'
                && data[i + 6] == 'P' && data[i + 7] == 'E')
            {
                isAnimated = true;
                break;
            }
        }

        return new ImageInfo(ImageFormat.Gif, width, height, isAnimated);
    }

    private static ImageInfo ProbeBmp(ReadOnlySpan<byte> data)
    {
        // DIB 头：BITMAPINFOHEADER 的宽高在字节 18..25（little-endian，有符号）。
        if (data.Length < 26)
            return new ImageInfo(ImageFormat.Bmp, 0, 0, false);

        var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..22]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[22..26]);
        return new ImageInfo(ImageFormat.Bmp, width, Math.Abs(height), false);
    }

    private static ImageInfo ProbeWebp(ReadOnlySpan<byte> data)
    {
        // RIFF 容器：VP8 / VP8L / VP8X 三个块头。
        if (data.Length < 30)
            return new ImageInfo(ImageFormat.WebP, 0, 0, false);

        if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == ' ')
        {
            // 有损 VP8：关键帧头在偏移 26（宽高 14 位各）。
            var width = ((data[26] | (data[27] << 8)) & 0x3FFF);
            var height = ((data[28] | (data[29] << 8)) & 0x3FFF);
            return new ImageInfo(ImageFormat.WebP, width, height, false);
        }

        if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'L')
        {
            // 无损 VP8L：14 位宽高藏在字节 21..24。
            if (data.Length < 25)
                return new ImageInfo(ImageFormat.WebP, 0, 0, false);
            var bits = data[21] | (data[22] << 8) | (data[23] << 16);
            var width = (bits & 0x3FFF) + 1;
            var height = ((bits >> 14) & 0x3FFF) + 1;
            return new ImageInfo(ImageFormat.WebP, width, height, false);
        }

        if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'X')
        {
            // 扩展容器：canvas 宽高在字节 24..29（24 位 + 1）。
            if (data.Length < 30)
                return new ImageInfo(ImageFormat.WebP, 0, 0, false);
            var width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            var height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            // 动画标志在字节 20 的 bit 1。
            var isAnimated = (data[20] & 0x02) != 0;
            return new ImageInfo(ImageFormat.WebP, width, height, isAnimated);
        }

        return new ImageInfo(ImageFormat.WebP, 0, 0, false);
    }

    /// <summary>
    /// 从 base64 文本里取出图像数据。
    ///
    /// 接受裸 base64，也接受 data URI（<c>data:image/png;base64,…</c>）。
    /// 解析失败返回 false 而不是抛异常 —— 远端数据不可信。
    /// </summary>
    public static bool TryDecodeBase64(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                return false;
            value = value[(comma + 1)..];
        }

        value = value.Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
        if (value.Length == 0)
            return false;

        // base64 长度必须是 4 的倍数；长度不对的按「补齐」处理。
        var padding = value.Length % 4;
        if (padding == 1)
            return false;
        if (padding != 0)
            value = value.PadRight(value.Length + (4 - padding), '=');

        try
        {
            data = Convert.FromBase64String(value);
            return data.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>base64 图像一步到位：解出数据并探测格式与尺寸。</summary>
    public static bool TryProbeBase64(string? text, out ImageInfo info)
    {
        info = default;
        if (!TryDecodeBase64(text, out var data))
            return false;

        info = Probe(data);
        return info.IsKnown;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static string AsText(ReadOnlySpan<byte> data)
    {
        var length = Math.Min(data.Length, 1024);
        return System.Text.Encoding.UTF8.GetString(data[..length]);
    }
}
