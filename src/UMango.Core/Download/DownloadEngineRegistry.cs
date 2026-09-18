using UMango.Core.Download.Engines;
using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载引擎注册表：按协议与可用性挑选最合适的引擎。
///
/// 优先级设计：
///   1. <see cref="Aria2Engine"/> —— 支持 HTTP/FTP/BT/元链接，多连接分片最快；装了就用它；
///   2. <see cref="Ed2kEngine"/>   —— 仅当协议是 eD2k 时考虑（aria2 不支持 eD2k）；
///   3. <see cref="BuiltinHttpEngine"/> —— 永远可用的兜底，无外部依赖、无许可负担。
///
/// 这样「弃用 PCL2 网页下载代码、集成 aria2/bittorrent/ed2k」在实现上落成：
/// 能力由引擎组合提供，缺哪个就降级到下一个，而不是整条链路失败。
/// </summary>
public sealed class DownloadEngineRegistry
{
    private readonly List<IDownloadEngine> _engines;

    public DownloadEngineRegistry(IEnumerable<IDownloadEngine>? engines = null)
        => _engines = engines?.ToList() ?? [new Aria2Engine(), new Ed2kEngine(), new BuiltinHttpEngine()];

    public IReadOnlyList<IDownloadEngine> All => _engines;

    /// <summary>当前可用的引擎（供设置页/诊断页展示）</summary>
    public IReadOnlyList<IDownloadEngine> Available => _engines.Where(e => e.IsAvailable).ToList();

    /// <summary>某个协议由哪些引擎支持（含不可用的，便于诊断"为什么没有 BT 支持"）</summary>
    public IReadOnlyList<IDownloadEngine> ForProtocol(DownloadProtocol protocol) =>
        _engines.Where(e => e.Protocols.Contains(protocol)).ToList();

    /// <summary>为给定链接挑选引擎；一个都没有时返回 null。</summary>
    public IDownloadEngine? Select(string url)
    {
        var protocol = DownloadProtocolExtensions.Detect(url);

        // eD2k 只有专用客户端能处理，优先按协议精确匹配
        if (protocol == DownloadProtocol.Ed2k)
            return _engines.FirstOrDefault(e => e.IsAvailable && e.Protocols.Contains(DownloadProtocol.Ed2k));

        // 其余协议：按注册顺序取第一个「可用且支持」的（注册顺序即优先级）
        return _engines.FirstOrDefault(e => e.IsAvailable && e.Protocols.Contains(protocol));
    }

    /// <summary>执行下载（自动挑引擎）。</summary>
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        TerminalBridge? bridge = null,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var engine = Select(request.Url);
        if (engine is null)
        {
            var protocol = DownloadProtocolExtensions.Detect(request.Url);
            var msg = protocol == DownloadProtocol.Ed2k
                ? "未检测到 eD2k 客户端（aMule / mldonkey），无法处理 ed2k 链接"
                : $"没有可用的下载引擎处理 {protocol.DisplayName()} 链接";
            bridge?.Write(true, msg);
            return new DownloadResult(false, null, "cli_missing", msg, 0);
        }

        bridge?.Write(false, $"使用下载引擎：{engine.DisplayName}（{engine.Id}）");
        return await engine.DownloadAsync(request, bridge, onProgress, ct).ConfigureAwait(false);
    }
}
