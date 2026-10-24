namespace UMango.Core.Download;

/// <summary>下载协议。用于按协议选择下载引擎。</summary>
public enum DownloadProtocol
{
    Http, Https, Ftp, Sftp, BitTorrent, Metalink, Ed2k, Unknown
}

public static class DownloadProtocolExtensions
{
    public static string DisplayName(this DownloadProtocol p) => p switch
    {
        DownloadProtocol.Http => "HTTP",
        DownloadProtocol.Https => "HTTPS",
        DownloadProtocol.Ftp => "FTP",
        DownloadProtocol.Sftp => "SFTP",
        DownloadProtocol.BitTorrent => "BitTorrent",
        DownloadProtocol.Metalink => "Metalink",
        DownloadProtocol.Ed2k => "eD2k",
        _ => "未知协议"
    };

    /// <summary>从 URL 或磁力/ed2k 链接推断协议。</summary>
    public static DownloadProtocol Detect(string link)
    {
        var s = link.Trim();
        if (s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.BitTorrent;
        if (s.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Ed2k;
        if (s.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.BitTorrent;
        if (s.EndsWith(".meta4", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith(".metalink", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Metalink;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Https;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Http;
        if (s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Ftp;
        if (s.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)) return DownloadProtocol.Sftp;
        return DownloadProtocol.Unknown;
    }
}

/// <summary>一次下载请求。</summary>
public sealed record DownloadRequest(
    string Url,
    string OutputDirectory,
    string? OutputFileName = null,
    /// <summary>期望的校验值（可选）。aria2 支持 --checksum，交给它校验更可靠。</summary>
    string? ExpectedSha256 = null,
    /// <summary>限速（字节/秒），空表示不限。</summary>
    long? MaxBytesPerSecond = null,
    /// <summary>
    /// 目标文件已存在且内容正确时是否**跳过**（PCL 的 <c>DownloadFile</c> 语义）。
    ///
    /// 默认 false —— 保持"显式要求才跳过"，避免用户点了"重新下载"却被悄悄跳过。
    /// </summary>
    bool SkipExisting = false);

/// <summary>下载进度快照。**拿不到就保持 null，不编造**。</summary>
public sealed record DownloadProgress(
    double? Fraction,
    long? BytesDone,
    long? BytesTotal,
    string? SpeedText,
    string? EtaText,
    string? Gid);

public sealed record DownloadResult(
    bool Success,
    string? OutputPath,
    string? ErrorCode,
    string? Message,
    long DurationMs);
