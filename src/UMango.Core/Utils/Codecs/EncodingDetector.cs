// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Codecs/EncodingDetector.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Codecs → UMango.Core.Utils.Codecs。
//   2. **修掉原版的致命缺陷**：原版的 `_IsValidUtf8` 从来没有真的从流里读数据 ——
//      它 `stream.Position = originalPosition` 之后直接
//      `Encoding.UTF8.GetString(buffer)`，而 buffer 是**没被填过的全零数组**。
//      全零数组当然能无损往返 UTF-8，于是**每一个文件都被判成合法 UTF-8**，
//      BOM 之外的一切编码（GBK、Shift-JIS、Latin1…）全部误判。
//      表现为"打开 GBK 的文件全是乱码"，而且**永远查不出原因**。
//      UMango 改成真的把样本读进 buffer 再判断。
//   3. 原版 `_DetectByBom` 里 `if (actualRead != sampleLength) throw new Exception(...)`
//      用裸 Exception 且在**空文件**上直接抛 —— 读 0 字节 ≠ 请求的 0 字节时会炸。
//      UMango 改为空文件直接返回 UTF-8（空内容没有编码可言）。
//   4. 原版 UTF-16LE 的 BOM 判断 `buffer is [0xff, 0xfe]` 出现在 UTF-32LE 判断**之前**，
//      而 UTF-32LE 的 BOM 是 `FF FE 00 00` —— 也以 FF FE 开头。
//      原版先判 `[_, _, 0x00, 0x00]`（列表模式 `is` 要求**完全相等**，
//      长度 4 的模式匹配 4 元素数组，而此时 sampleLength 只有 2 时不匹配），
//      结果 UTF-32LE 文件被识别成 UTF-16LE。UMango 按 BOM 长度严格判定。
//   5. 补 XML 文档。

using System.Text;

namespace UMango.Core.Utils.Codecs;

/// <summary>
/// 文本编码检测。
///
/// 判定顺序：BOM → 严格 UTF-8 校验 → GB18030 → Latin1（兜底，不丢字节）。
/// </summary>
public static class EncodingDetector
{
    /// <summary>取样长度：足够覆盖 UTF-32 的 4 字节 BOM。</summary>
    private const int SampleLength = 4;

    /// <summary>
    /// 检测流中的文本编码（流必须支持 Seek）。
    /// </summary>
    /// <param name="stream">输入流，必须可读且可 Seek。</param>
    /// <param name="readFromBegin">是否从头开始检测（否则从当前位置往后）。</param>
    /// <returns>检测到的编码；无法判定时返回 UTF-8。</returns>
    public static Encoding DetectEncoding(Stream stream, bool readFromBegin = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("流必须支持读操作。", nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("流必须支持 Seek 操作。", nameof(stream));

        var originalPosition = stream.Position;
        if (readFromBegin) stream.Seek(0, SeekOrigin.Begin);

        try
        {
            var start = stream.Position;
            var byBom = DetectByBom(stream, start);
            if (byBom is not null) return byBom;

            if (IsValidUtf8(stream, start)) return Utf8NoBom;

            // 不是合法 UTF-8 —— 先试 GBK（更常见），再试 GB18030（GBK 的超集）
            if (IsValidGbk(stream, start)) return Gbk;
            if (IsValidGb18030(stream, start)) return Encodings.GB18030;

            return Encoding.Latin1;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>检测字节序列的编码。</summary>
    public static Encoding DetectEncoding(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return DetectEncoding(new MemoryStream(bytes, writable: false), readFromBegin: true);
    }

    /// <summary>
    /// UTF-8，但**去掉 BOM**。
    ///
    /// 为什么单独提供：带 BOM 的 UTF-8 若按普通 UTF-8 解码，文件开头会多出一个
    /// U+FEFF 零宽不换行字符 —— 在界面上表现为"第一行前面多了一段空白"，
    /// 在解析 JSON 时会直接报错。
    /// </summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>GBK（代码页 936）—— 供检测层判断"这到底是 GBK 还是 GB18030"。</summary>
    public static Encoding Gbk => Encodings.Gbk;

    private static Encoding? DetectByBom(Stream stream, long start)
    {
        var buffer = ReadSample(stream, start);
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return Utf8NoBom;

        if (buffer.Length >= 4)
        {
            // UTF-32 必须先判：它的 BOM 以 FF FE / 00 00 FE FF 开头，
            // 与 UTF-16 的前缀重叠，先判 UTF-16 会把 UTF-32 误认。
            if (buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
                return TryGetEncoding("utf-32BE") ?? Encoding.UTF32;

            if (buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
                return Encoding.UTF32;
        }

        if (buffer.Length >= 2)
        {
            if (buffer[0] == 0xFE && buffer[1] == 0xFF)
                return Encoding.BigEndianUnicode;

            if (buffer[0] == 0xFF && buffer[1] == 0xFE)
                return Encoding.Unicode;
        }

        return null;
    }

    /// <summary>
    /// 严格 UTF-8 校验。
    ///
    /// 用 <see cref="UTF8Encoding"/> 的**抛异常**回退：
    /// 默认的 <see cref="Encoding.UTF8"/> 遇到非法字节会替换成 U+FFFD 而不报错，
    /// 那样"是不是 UTF-8"就永远判断不出来。
    /// </summary>
    private static bool IsValidUtf8(Stream stream, long start)
    {
        var buffer = ReadSample(stream, start, 4096);
        if (buffer.Length == 0) return true;

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(buffer);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// GBK（代码页 936）—— 简体中文最常见的编码。
    ///
    /// 为什么要单独判 GBK：GB18030 是 GBK 的**超集**，任何 GBK 文本
    /// 用 GB18030 解都能成功 —— 所以只判 GB18030 的话永远分不出
    /// "这到底是 GBK 还是 GB18030"，编码名会一律报成 GB18030。
    /// </summary>
    private static bool IsValidGbk(Stream stream, long start)
    {
        var encoding = Gbk;
        if (encoding.CodePage != 936) return false; // 拿不到真正的 GBK，别乱判

        var buffer = ReadSample(stream, start, 4096);
        if (buffer.Length == 0) return true;

        try
        {
            var strict = (Encoding)encoding.Clone();
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;
            strict.GetString(buffer);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidGb18030(Stream stream, long start)
    {
        var encoding = Encodings.GB18030;
        if (encoding.CodePage != 54936) return false; // 拿不到真正的 GB18030，别乱判

        var buffer = ReadSample(stream, start, 4096);
        if (buffer.Length == 0) return true;

        try
        {
            var strict = (Encoding)encoding.Clone();
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;
            strict.GetString(buffer);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>从指定位置读一段样本；读不到就返回已读到的部分（可能为空）。</summary>
    private static byte[] ReadSample(Stream stream, long start, int maxLength = SampleLength)
    {
        stream.Position = start;
        var buffer = new byte[maxLength];
        var total = 0;
        while (total < maxLength)
        {
            var read = stream.Read(buffer, total, maxLength - total);
            if (read <= 0) break;
            total += read;
        }

        if (total == maxLength) return buffer;
        var trimmed = new byte[total];
        Array.Copy(buffer, trimmed, total);
        return trimmed;
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
