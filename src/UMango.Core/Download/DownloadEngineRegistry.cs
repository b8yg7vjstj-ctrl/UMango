using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载引擎注册表：按协议与可用性挑选最合适的引擎。
///
/// 优先级：aria2（支持 HTTP/FTP/BT/Metalink）> eD2k（仅 eD2k）> 内置 HttpClient（兜底）。
/// 不是"只有一个引擎硬编码"——缺哪个就降级，而不是整条链路失败。
/// </summary>
public sealed class DownloadEngineRegistry
{
    private readonly List<IDownloadEngine> _engines;

    public DownloadEngineRegistry(IEnumerable<IDownloadEngine>? engines = null)
        => _engines = engines?.ToList() ?? [new Engines.Aria2Engine(), new Engines.Ed2kEngine(), new Engines.BuiltinHttpEngine()];

    public IReadOnlyList<IDownloadEngine> All => _engines;
    public IReadOnlyList<IDownloadEngine> Available => _engines.Where(e => e.IsAvailable).ToList();

    public IDownloadEngine? Select(string link)
    {
        var protocol = DownloadProtocolExtensions.Detect(link);
        if (protocol == DownloadProtocol.Ed2k)
            return _engines.FirstOrDefault(e => e.IsAvailable && e.Protocols.Contains(DownloadProtocol.Ed2k));
        return _engines.FirstOrDefault(e => e.IsAvailable && e.Protocols.Contains(protocol));
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, TerminalBridge? bridge = null,
        Action<DownloadProgress>? onProgress = null, CancellationToken ct = default)
    {
        var engine = Select(request.Url);
        if (engine is null)
            return new DownloadResult(false, null, "cli_missing", "没有可用的下载引擎处理该链接", 0);
        bridge?.Write(false, $"使用下载引擎：{engine.DisplayName}（{engine.Id}）");
        return await engine.DownloadAsync(request, bridge, onProgress, ct).ConfigureAwait(false);
    }
}
