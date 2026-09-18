using System.Diagnostics;
using UMango.Core.Execution;
using UMango.Core.Terminal;

namespace UMango.Core.Download.Engines;

/// <summary>
/// aria2 下载引擎：HTTP / HTTPS / FTP / SFTP / BitTorrent / Metalink 一把梭。
///
/// **许可说明（硬性约束）**：aria2 以 **GPL-2.0** 分发。
/// 因此本引擎**只以「独立进程 + 命令行」方式调用系统上已安装的 aria2c**（法律上属聚合），
/// **不链接其代码、不随包分发其二进制**。用户需自行安装（macOS: brew install aria2）。
///
/// **参数取舍（都来自真机实测）**：
///   · <c>--enable-color=false</c>：否则输出带 ANSI 颜色码，解析前还得剥；
///   · <c>LC_ALL=C</c>：aria2 的消息是**本地化**的（中文环境输出「下载完成：」），
///     强制英文可以得到稳定可解析的输出（解析器仍兼容中文）；
///   · <c>--summary-interval=1</c>：每秒打一次进度读数，进度界面才有东西可显示；
///   · <c>--file-allocation=none</c>：省掉预分配磁盘空间的等待与提示行，包下载通常不大；
///   · <c>--seed-time=0</c>：**关键** —— 下载种子后默认会继续做种，进程不退出，
///     界面看起来就像"卡住了"。设为 0 表示下载完即停。
///   · <c>--max-tries</c> / <c>--retry-wait</c>：网络抖动时的兜底重试。
/// </summary>
public sealed class Aria2Engine : IDownloadEngine
{
    public string Id => "aria2";

    public string DisplayName => "aria2";

    public IReadOnlyList<DownloadProtocol> Protocols =>
    [
        DownloadProtocol.Http, DownloadProtocol.Https, DownloadProtocol.Ftp,
        DownloadProtocol.Sftp, DownloadProtocol.BitTorrent, DownloadProtocol.Metalink
    ];

    /// <summary>本机是否装了 aria2c（探测一次并缓存）</summary>
    public string? ExecutablePath { get; }

    public bool IsAvailable => ExecutablePath is not null;

    public string LicenseNote =>
        "aria2 以 GPL-2.0 分发。本程序**不链接也不分发**其代码或二进制，" +
        "仅以独立进程方式调用你系统上已安装的 aria2c（属聚合使用）。" +
        "安装方式：macOS 用 brew install aria2；Linux 用各发行版包管理器；Windows 用 scoop/winget install aria2。";

    public Aria2Engine(string? executablePath = null)
        => ExecutablePath = executablePath ?? ProcessRunner.Which("aria2c");

    /// <summary>组装 aria2c 命令行（抽出来便于测试与展示）。</summary>
    public static IReadOnlyList<string> BuildArgs(DownloadRequest request)
    {
        var protocol = DownloadProtocolExtensions.Detect(request.Url);
        var isTorrent = protocol is DownloadProtocol.BitTorrent or DownloadProtocol.Metalink;

        var args = new List<string>
        {
            "--enable-color=false",
            "--console-log-level=warn",
            "--show-console-readout=true",
            "--summary-interval=1",
            "--file-allocation=none",
            "--continue=true",          // 断点续传
            "--max-tries=3",
            "--retry-wait=2",
            "--max-connection-per-server=8",
            "--split=8",
            "--min-split-size=1M",
            "-d", request.OutputDirectory,
        };

        // 种子/元链接：不要 -o（文件名由种子内容决定），且必须禁止做种否则进程不退出
        if (isTorrent)
        {
            args.Add("--seed-time=0");
            args.Add("--bt-stop-timeout=60");
        }
        else if (!string.IsNullOrWhiteSpace(request.OutputFileName))
        {
            args.Add("-o");
            args.Add(request.OutputFileName!);
        }

        if (request.MaxBytesPerSecond is > 0)
        {
            args.Add($"--max-download-limit={request.MaxBytesPerSecond}");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            args.Add($"--checksum=sha-256={request.ExpectedSha256}");
        }

        args.Add(request.Url);
        return args;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        TerminalBridge? bridge,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            const string msg = "未找到 aria2c";
            bridge?.Write(true, msg);
            return new DownloadResult(false, null, "cli_missing", LicenseNote, 0);
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var sw = Stopwatch.StartNew();
        var lastFraction = -1.0;
        string? completedPath = null;
        var sawError = false;
        var errorText = "";

        void OnLine(bool isStdErr, string line)
        {
            bridge?.Write(isStdErr, line);

            if (Aria2OutputParser.IsError(line)) { sawError = true; errorText = line; }

            if (Aria2OutputParser.IsCompletion(line, out var path) && !string.IsNullOrWhiteSpace(path))
                completedPath = path;

            var p = Aria2OutputParser.TryParseProgress(line);
            if (p is not null)
            {
                // 只在百分比真正变化时回调，避免每秒重复触发界面刷新
                if (p.Fraction is { } f && Math.Abs(f - lastFraction) > 0.0001)
                {
                    lastFraction = f;
                    onProgress?.Invoke(p);
                }
            }
        }

        var spec = new ProcessSpec
        {
            Program = ExecutablePath!,
            Args = BuildArgs(request).ToList(),
            // 种子/元链接可能很慢；大文件也一样。给足 2 小时，由用户主动取消。
            TimeoutMs = 2 * 60 * 60 * 1000,
            WorkingDirectory = request.OutputDirectory,
        };

        // aria2 的消息随 locale 变化，强制英文以获得稳定可解析的输出
        spec.Env.Add(("LC_ALL", "C"));

        ProcessOutput output;
        try
        {
            output = await ProcessRunner.RunAsync(spec, OnLine, ct).ConfigureAwait(false);
        }
        catch (ProcessException ex)
        {
            bridge?.Write(true, ex.Message);
            return new DownloadResult(false, null, ex.Code, ex.Message, sw.ElapsedMilliseconds);
        }

        var elapsed = sw.ElapsedMilliseconds;

        if (output.TimedOut)
        {
            const string msg = "下载超时";
            bridge?.Write(true, msg);
            return new DownloadResult(false, null, "timeout", msg, elapsed);
        }

        if (!output.Success || sawError)
        {
            var msg = string.IsNullOrWhiteSpace(errorText)
                ? $"aria2c 退出码 {output.ExitCode}"
                : errorText;
            return new DownloadResult(false, null, "download_failed", msg, elapsed);
        }

        // 解析不到 completion 行时，退而求其次：按请求推断输出路径
        var path2 = completedPath;
        if (string.IsNullOrWhiteSpace(path2) && !string.IsNullOrWhiteSpace(request.OutputFileName))
            path2 = Path.Combine(request.OutputDirectory, request.OutputFileName!);

        return new DownloadResult(true, path2, null, null, elapsed);
    }
}
