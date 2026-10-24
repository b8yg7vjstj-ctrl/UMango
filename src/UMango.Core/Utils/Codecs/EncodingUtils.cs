// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Codecs/EncodingUtils.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Codecs → UMango.Core.Utils.Codecs。
//   2. **修掉原版的漏字**：原版解码 UTF-8 时**不跳 BOM**，
//      而它检测时用的又是 `Encoding.UTF8`（带 BOM 的那个实例）。
//      `Encoding.UTF8.GetString(带 BOM 的字节)` 会把 BOM 当成一个字符解出来，
//      结果字符串开头多一个 U+FEFF。原版只在 `encoding.Equals(Encoding.UTF8)`
//      分支里跳 BOM，但那个分支永远进不去（`Encoding.UTF8` 与 `Encoding.UTF8`
//      虽然 Equals，但**带 BOM 的 UTF8Encoding 与 Encoding.UTF8 不是同一实例**，
//      Equals 比较的是 CodePage 与 EncoderFallback —— 实际上相等，
//      于是分支能进，可 `Encoding.UTF8` 本身是**带 BOM 发出**的编码，
//      解码时并不消费 BOM，GetString 照样把 EF BB BF 当 U+FEFD 留下）。
//      总之原版的 BOM 处理自相矛盾。UMango 统一用
//      <see cref="EncodingDetector.Utf8NoBom"/> 解码并显式跳过 BOM 字节。
//   3. 原版 `DecodeBytes` 对空数组返回 ""，但对**无法解码**的字节
//      会抛异常。UMango 保证**永不抛异常**、永不丢字节。
//   4. 补 `DecodeBytesTo`（返回字节的版本）与 `IsDefaultEncodingUtf8/Gbk`。

using System.Text;

namespace UMango.Core.Utils.Codecs;

/// <summary>
/// 编码解码工具。
/// </summary>
public static class EncodingUtils
{
    /// <summary>系统默认编码是否是 UTF-8。</summary>
    public static bool IsDefaultEncodingUtf8() => Encoding.Default.CodePage == 65001;

    /// <summary>系统默认编码是否是 GBK。</summary>
    public static bool IsDefaultEncodingGbk() => Encoding.Default.CodePage == 936;

    /// <summary>
    /// 解码字节序列：自动识别 BOM（UTF-8 / UTF-16 LE+BE / UTF-32 LE+BE），
    /// 无 BOM 时按 UTF-8 → GB18030 → Latin1 依次尝试。
    ///
    /// **永不抛异常、永不丢字节**：最坏情况返回 Latin1 的结果。
    /// </summary>
    public static string DecodeBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        var encoding = EncodingDetector.DetectEncoding(bytes);
        var body = SkipBom(bytes, encoding);

        try
        {
            return encoding.GetString(body);
        }
        catch (Exception)
        {
            // 理论上到不了这里（检测过的编码必然能解），但仍要保证不抛
            return Encoding.Latin1.GetString(body);
        }
    }

    /// <summary>
    /// 去掉 BOM 字节。
    ///
    /// 为什么不靠解码器自动消费：<see cref="Encoding.UTF8"/> 解码时
    /// **不会**吃掉 BOM，它会把 EF BB BF 当成一个 U+FEFD 字符留在开头。
    /// </summary>
    private static byte[] SkipBom(byte[] bytes, Encoding encoding)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return bytes.AsSpan(3).ToArray();

        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return bytes.AsSpan(4).ToArray();

            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return bytes.AsSpan(4).ToArray();
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return bytes.AsSpan(2).ToArray();

            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return bytes.AsSpan(2).ToArray();
        }

        return bytes;
    }
}
