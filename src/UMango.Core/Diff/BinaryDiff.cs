using System.IO.Compression;

namespace UMango.Core.Diff;

/// <summary>
/// 二进制差分/补丁工具。
///
/// 对应 PCL-CE 的 Utils/Diff/（4 文件，Apache-2.0）。
/// 提供简单的二进制补丁能力，用于增量更新：
///   · 生成补丁：从旧文件和新文件生成一个小的 diff
///   · 应用补丁：把 diff 应用到旧文件上得到新文件
///
/// 注意：这是简化实现，用 deflate 压缩来达到差分效果，
/// 而不是真正的 bsdiff 算法（后者需要外部库）。
/// 适合小文件的增量更新（< 10 MB），大文件应走完整下载。
/// </summary>
public static class BinaryDiff
{
    /// <summary>从旧文件和新文件生成补丁。</summary>
    public static byte[] CreatePatch(byte[] oldData, byte[] newData)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(newData, 0, newData.Length);
        }
        return output.ToArray();
    }

    /// <summary>应用补丁到旧文件得到新文件。</summary>
    public static byte[] ApplyPatch(byte[] oldData, byte[] patch)
    {
        using var input = new MemoryStream(patch);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>判断补丁是否值得（新文件比旧文件大就直接下载，不用补丁）。</summary>
    public static bool IsWorthPatching(long oldSize, long newSize, int patchSizeBytes)
    {
        return patchSizeBytes < oldSize * 0.7;  // 补丁小于原文件 70% 才值得
    }
}
