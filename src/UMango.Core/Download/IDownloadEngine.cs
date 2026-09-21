using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载引擎契约。
///
/// **许可约束（硬性）**：
/// 本层接入的第三方下载工具多为 GPL 系（aria2 为 GPL-2.0、aMule 为 GPL-2.0）。
/// 因此引擎**只能以「独立进程 + 命令行」的方式调用它们**（法律上属"聚合"），
/// **不得把它们的代码静态或动态链接进本程序**。
/// </summary>
public interface IDownloadEngine
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<DownloadProtocol> Protocols { get; }
    string? ExecutablePath { get; }
    bool IsAvailable { get; }
    string LicenseNote { get; }
    Task<DownloadResult> DownloadAsync(DownloadRequest request, TerminalBridge? bridge,
        Action<DownloadProgress>? onProgress, CancellationToken ct = default);
}
