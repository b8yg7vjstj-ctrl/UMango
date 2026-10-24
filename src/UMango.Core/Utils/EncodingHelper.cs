using System.Text;
using UMango.Core.Utils.Codecs;

namespace UMango.Core.Utils;

/// <summary>
/// 编码检测与转换工具（**旧接口**）。
///
/// 对应 PCL-CE 的 Utils/Codecs/（3 文件，Apache-2.0）。
/// CLI 输出在不同系统上用不同编码：
///   Node/npm → UTF-8，Git（Windows）→ system codepage（GBK），PowerShell → UTF-16LE。
/// 检测 UTF-8 有效性，若无效退到常见编码恢复文本；永远不丢数据。
///
/// **现在只做转发**：真正的实现已迁到
/// <see cref="UMango.Core.Utils.Codecs"/>（与 PCL-CE 同名同构），
/// 本类保留是为了不破坏既有调用方。
///
/// 旧实现有个**顺序依赖的隐患**：它直接调 `Encoding.GetEncoding(936)`，
/// 而 .NET Core 默认不提供代码页 936 —— 能不能跑通取决于
/// "有没有别的代码先注册过 CodePagesEncodingProvider"。
/// 转发到新层之后，注册由 <see cref="Encodings"/> 惰性完成，不再依赖巧合。
/// </summary>
public static class EncodingHelper
{
    /// <summary>判断字节序列是否是有效的 UTF-8。</summary>
    public static bool IsValidUtf8(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return true;

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 自动解码字节序列：先试 UTF-8，无效则退到 GB18030/Latin1。
    /// 返回值永远非 null——最坏情况是 Latin1（每字节一个字符，不丢数据）。
    /// </summary>
    public static string DecodeAuto(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return "";

        return EncodingUtils.DecodeBytes(bytes);
    }

    /// <summary>检测字节序列的编码名称。</summary>
    public static string DetectEncoding(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return "UTF-8";

        var encoding = EncodingDetector.DetectEncoding(bytes);
        return encoding.CodePage switch
        {
            65001 => "UTF-8",
            54936 => "GB18030",
            936 => "GBK",
            12000 => "UTF-32",
            12001 => "UTF-32BE",
            1201 => "UTF-16",
            1200 => "UTF-16BE",
            28591 => "Latin1",
            _ => encoding.WebName
        };
    }
}
