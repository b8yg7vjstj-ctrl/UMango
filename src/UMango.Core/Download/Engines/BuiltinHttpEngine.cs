using System.Diagnostics;
using System.Net.Http;
using UMango.Core.Network;
using UMango.Core.Terminal;

namespace UMango.Core.Download.Engines;

/// <summary>
/// 内置 HTTP/HTTPS 下载引擎：**.NET 自带 HttpClient 实现，零外部依赖、零许可负担**。
///
/// 存在的意义：aria2 未安装时（或用户不想装 GPL 工具时）仍能下载。
/// 代价是没有多连接分片、没有 BT/元链接支持 —— 因此优先级低于 aria2，
/// 只在 aria2 不可用时兜底。
/// </summary>
public sealed class BuiltinHttpEngine : IDownloadEngine
{
    public string Id => "builtin-http";

    public string DisplayName => "内置下载（HttpClient）";

    public IReadOnlyList<DownloadProtocol> Protocols => [DownloadProtocol.Http, DownloadProtocol.Https];

    /// <summary>永远可用：不依赖任何外部程序</summary>
    public string? ExecutablePath => null;

    public bool IsAvailable => true;

    public string LicenseNote =>
        "使用 .NET 自带的 HttpClient 实现，不引入任何第三方下载库，无额外许可义务。" +
        "不支持 BitTorrent / 元链接 / eD2k；也没有多连接分片，速度不如 aria2。";

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        TerminalBridge? bridge,
        Action<DownloadProgress>? onProgress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(request.OutputDirectory);

        var fileName = request.OutputFileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            var last = request.Url.Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
            fileName = string.IsNullOrWhiteSpace(last) ? "download.bin" : last;
        }
        var target = Path.Combine(request.OutputDirectory, fileName!);

        try
        {
            // 用全局代理管理器建 handler：用户设了代理，下载也必须走代理。
            using var http = new HttpClient(ProxyHolder.CreateHandler()) { Timeout = TimeSpan.FromHours(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UMango/0.1");

            using var resp = await http.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            bridge?.Write(true, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode)
            {
                return new DownloadResult(false, null, "download_failed",
                    $"HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds);
            }

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(target);

            var buffer = new byte[81920];
            long done = 0;
            double lastReported = -1;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                if (total is > 0)
                {
                    var fraction = (double)done / total.Value;
                    // 只在变化超过 0.5% 时上报，避免刷爆界面
                    if (fraction - lastReported >= 0.005 || fraction >= 1.0)
                    {
                        lastReported = fraction;
                        onProgress?.Invoke(new DownloadProgress(
                            Fraction: Math.Clamp(fraction, 0, 1),
                            BytesDone: done, BytesTotal: total,
                            SpeedText: null, EtaText: null, Gid: null));
                    }
                }
                else
                {
                    // 服务端没给 Content-Length：**不编造百分比**，只上报已下载字节
                    onProgress?.Invoke(new DownloadProgress(
                        Fraction: null, BytesDone: done, BytesTotal: null,
                        SpeedText: null, EtaText: null, Gid: null));
                }
            }

            sw.Stop();
            return new DownloadResult(true, target, null, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            TryDelete(target);
            return new DownloadResult(false, null, "canceled", "下载已取消", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            TryDelete(target);
            return new DownloadResult(false, null, "download_failed", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }
}
