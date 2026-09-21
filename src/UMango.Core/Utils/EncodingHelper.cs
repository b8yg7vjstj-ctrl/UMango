using System.Text;

namespace UMango.Core.Utils;

/// <summary>
/// 编码检测与转换工具。
///
/// 对应 PCL-CE 的 Utils/Codecs/（3 文件，Apache-2.0）。
/// CLI 输出在不同系统上用不同编码：
///   Node/npm → UTF-8，Git（Windows）→ system codepage（GBK），PowerShell → UTF-16LE。
/// 检测 UTF-8 有效性，若无效退到常见编码恢复文本；永远不丢数据。
/// </summary>
public static class EncodingHelper
{
    /// <summary>判断字节序列是否是有效的 UTF-8。</summary>
    public static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            return !decoded.Contains('\uFFFD');
        }
        catch { return false; }
    }

    /// <summary>
    /// 自动解码字节序列：先试 UTF-8，无效则退到 GBK/Latin1。
    /// 返回值永远非 null——最坏情况是 Latin1（每字节一个字符，不丢数据）。
    /// </summary>
    public static string DecodeAuto(byte[] bytes)
    {
        if (IsValidUtf8(bytes)) return Encoding.UTF8.GetString(bytes);

        foreach (var enc in new[] { Encoding.GetEncoding(936), Encoding.UTF8, Encoding.Latin1 })
        {
            try
            {
                var str = enc.GetString(bytes);
                if (!str.Contains('\uFFFD')) return str;
            }
            catch { /* 编码不可用则跳过 */ }
        }
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>检测字节序列的编码名称。</summary>
    public static string DetectEncoding(byte[] bytes)
    {
        if (IsValidUtf8(bytes)) return "UTF-8";
        try
        {
            var gbk = Encoding.GetEncoding(936);
            var str = gbk.GetString(bytes);
            if (!str.Contains('\uFFFD')) return "GBK";
        }
        catch { }
        return "Latin1";
    }
}
