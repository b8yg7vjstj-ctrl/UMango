using System.IO.Compression;

namespace UMango.Core.Diff;

/// <summary>
/// 二进制差分/补丁工具。
///
/// 对应 PCL-CE 的 Utils/Diff/（4 文件，Apache-2.0）。
/// 提供二进制补丁能力，用于增量更新：
///   · 生成补丁：从旧文件和新文件生成一个小的 diff
///   · 应用补丁：把 diff 应用到旧文件上得到新文件
///
/// 实现说明：
/// PCL-CE 的 <c>BsDiff</c> **只有应用侧**（<c>MakeAsync</c> 直接抛
/// <c>NotImplementedException</c>），且其指令集无法引用旧文件本身，
/// 「文件尾部完全相同」这种最常见的更新场景也得把整段尾巴塞进 extra 块。
/// UMango 用 <see cref="BinaryDelta"/>（公共前缀/后缀引用旧文件 + 差分 + 额外块）
/// 补齐了生成侧，补丁体积因此能接近真正增量的下限。
///
/// 为兼容早期用 deflate 直接压缩新文件产生的旧补丁，
/// <see cref="ApplyPatch"/> 会识别补丁类型：带 UMD2 魔数走增量引擎，
/// 否则回落到旧的 deflate 路径。
/// </summary>
public static class BinaryDiff
{
    /// <summary>从旧文件和新文件生成补丁。</summary>
    public static byte[] CreatePatch(byte[] oldData, byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(newData);
        return BinaryDelta.Create(oldData, newData);
    }

    /// <summary>应用补丁到旧文件得到新文件。</summary>
    public static byte[] ApplyPatch(byte[] oldData, byte[] patch)
    {
        ArgumentNullException.ThrowIfNull(oldData);
        ArgumentNullException.ThrowIfNull(patch);

        if (BinaryDelta.IsPatch(patch))
            return BinaryDelta.Apply(oldData, patch);

        // 旧补丁：deflate 压缩的完整新文件。
        using var input = new MemoryStream(patch);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// 判断补丁是否值得（补丁明显小于原文件才值得走增量，否则直接下载新文件）。
    /// </summary>
    public static bool IsWorthPatching(long oldSize, long newSize, int patchSizeBytes)
    {
        return patchSizeBytes < oldSize * 0.7;  // 补丁小于原文件 70% 才值得
    }
}
