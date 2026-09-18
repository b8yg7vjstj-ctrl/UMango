using System.Diagnostics;
using UMango.Core.Execution;
using UMango.Core.Terminal;

namespace UMango.Core.Download.Engines;

/// <summary>
/// eD2k 下载引擎。
///
/// **为什么它和 aria2 不一样**：aria2 **不支持** eD2k 协议。
/// eD2k 需要专用的 P2P 客户端（aMule / mldonkey），而且这些客户端通常以
/// **常驻守护进程**方式运行，本身维护自己的下载目录与队列 ——
/// 命令行只能把链接"投递"给它，进度要到它的接口里查。
///
/// **许可说明**：aMule 与 mldonkey 均为 **GPL-2.0**。
/// 与 aria2 同样的处理：**只以独立进程方式调用，不链接、不分发**。
///
/// ⚠️ **验证状态**：本机没有安装任何 eD2k 客户端（amule / mldonkey 都不存在），
/// 因此本引擎的**投递逻辑未经真机验证**。检测与降级路径是实测过的
/// （见单元测试），投递命令依据各客户端文档编写。
/// </summary>
public sealed class Ed2kEngine : IDownloadEngine
{
    /// <summary>按优先级探测的 eD2k 客户端</summary>
    private static readonly (string Cli, string Name, string[] LinkArgs)[] Candidates =
    [
        ("amule",   "aMule",     ["--quiet"]),
        ("amuled",  "aMule 守护进程", []),
        ("mlnet",   "mldonkey",  []),
        ("mldonkey","mldonkey",  []),
    ];

    private readonly (string Cli, string Name, string[] LinkArgs)? _client;

    public Ed2kEngine(string? overrideCli = null)
    {
        if (overrideCli is not null)
        {
            _client = (overrideCli, Path.GetFileNameWithoutExtension(overrideCli), []);
            return;
        }
        foreach (var c in Candidates)
        {
            if (ProcessRunner.Which(c.Cli) is not null) { _client = c; return; }
        }
        _client = null;
    }

    public string Id => "ed2k-client";

    public string DisplayName => _client?.Name ?? "eD2k（未安装客户端）";

    public IReadOnlyList<DownloadProtocol> Protocols => [DownloadProtocol.Ed2k];

    public string? ExecutablePath => _client is null ? null : ProcessRunner.Which(_client.Value.Cli);

    public bool IsAvailable => _client is not null && ExecutablePath is not null;

    public string LicenseNote =>
        "eD2k 需要外部 P2P 客户端（aMule / mldonkey），二者均为 GPL-2.0。" +
        "本程序**不链接也不分发**它们，仅以独立进程方式把链接投递给已安装的客户端（属聚合使用）。" +
        "本机未检测到 eD2k 客户端，因此该引擎当前不可用；" +
        "安装 aMule（macOS: brew install amule；Linux: 发行版包管理器）后重启本程序即可启用。";

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        TerminalBridge? bridge,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            var msg = "未检测到 eD2k 客户端（aMule / mldonkey）";
            bridge?.Write(true, msg);
            // 明确告知用户"要装什么"，而不是含糊报错
            return new DownloadResult(false, null, "cli_missing", LicenseNote, 0);
        }

        var sw = Stopwatch.StartNew();
        var client = _client!.Value;
        var args = new List<string>(client.LinkArgs) { request.Url };

        void OnLine(bool isStdErr, string line) => bridge?.Write(isStdErr, line);

        var spec = new ProcessSpec
        {
            Program = ExecutablePath!,
            Args = args,
            TimeoutMs = 60_000,
        };

        try
        {
            var output = await ProcessRunner.RunAsync(spec, OnLine, ct).ConfigureAwait(false);
            sw.Stop();

            if (!output.Success)
            {
                var msg = $"投递给 {client.Name} 失败：{ProcessRunner.Which(client.Cli)}";
                return new DownloadResult(false, null, "command_failed", msg, sw.ElapsedMilliseconds);
            }

            // 链接已投递，但**下载由客户端自己的队列完成**，进度要到它的接口里查 ——
            // 因此这里如实返回"已投递"，而不是假装下载完成。
            return new DownloadResult(
                true, null, null,
                $"已把链接投递给 {client.Name}；后续下载与进度请在该客户端中查看（eD2k 由常驻客户端负责）。",
                sw.ElapsedMilliseconds);
        }
        catch (ProcessException ex)
        {
            sw.Stop();
            bridge?.Write(true, ex.Message);
            return new DownloadResult(false, null, ex.Code, ex.Message, sw.ElapsedMilliseconds);
        }
    }
}
