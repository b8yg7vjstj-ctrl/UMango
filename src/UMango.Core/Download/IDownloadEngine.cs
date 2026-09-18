using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载引擎契约。
///
/// **许可约束（硬性，见 THIRD-PARTY-NOTICES.md）**：
/// 本层接入的第三方下载工具多为 GPL 系（aria2 为 GPL-2.0、aMule 为 GPL-2.0）。
/// 因此引擎**只能以「独立进程 + 命令行」的方式调用它们**（法律上属"聚合"），
/// **不得把它们的代码静态或动态链接进本程序**，也不得随包分发其二进制。
/// 这一约束体现在：引擎只持有可执行文件路径并通过 ProcessRunner 调用。
/// </summary>
public interface IDownloadEngine
{
    /// <summary>稳定标识（"aria2" / "amule" / "builtin"）</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>支持的协议</summary>
    IReadOnlyList<DownloadProtocol> Protocols { get; }

    /// <summary>可执行文件路径；为 null 表示本机未安装、该引擎不可用</summary>
    string? ExecutablePath { get; }

    /// <summary>本机是否可用</summary>
    bool IsAvailable { get; }

    /// <summary>许可说明（展示给用户，合规要求）</summary>
    string LicenseNote { get; }

    /// <summary>
    /// 执行下载。
    /// </summary>
    /// <param name="request">下载请求</param>
    /// <param name="bridge">终端桥（可为 null）；引擎把原始输出回传到这里</param>
    /// <param name="onProgress">进度回调；解析不到进度时不调用（而不是报 0）</param>
    /// <param name="ct">取消令牌</param>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        TerminalBridge? bridge,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct = default);
}
