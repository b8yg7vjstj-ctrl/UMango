using System.Security.Cryptography;

namespace UMango.Core.Download;

/// <summary>
/// 一个待下载的文件：URL + 本地路径 + 期望校验值。
///
/// 这是 PCL-CE <c>Modules/Network/Models/DownloadFile.cs</c> 的语义
/// （"目标已存在且内容正确 → 跳过"），UMango 原先缺这一层。
/// </summary>
public sealed record DownloadFile(
    string Url,
    string TargetPath,
    /// <summary>期望的 SHA-256（小写十六进制）。空表示不校验。</summary>
    string? ExpectedSha256 = null)
{
    /// <summary>目标是否已经存在。</summary>
    public bool Exists => File.Exists(TargetPath);

    /// <summary>
    /// 目标存在且内容是否符合期望。
    ///
    /// 没有期望校验值时，**只按「存在且非空」判定** —— 宁可重下一次，
    /// 也不能把一个 0 字节的残留当成"已完成"。
    /// </summary>
    public bool IsComplete()
    {
        if (!File.Exists(TargetPath)) return false;

        if (string.IsNullOrWhiteSpace(ExpectedSha256))
        {
            try
            {
                return new FileInfo(TargetPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            return string.Equals(ComputeSha256(TargetPath), ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>计算文件的 SHA-256（小写十六进制）。</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
