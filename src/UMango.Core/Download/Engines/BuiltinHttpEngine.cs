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

        // ---- 目标已存在且内容正确 → 跳过（PCL 的 DownloadFile 语义）----
        if (request.SkipExisting)
        {
            var existing = new DownloadFile(request.Url, target, request.ExpectedSha256);
            if (existing.IsComplete())
            {
                sw.Stop();
                bridge?.Write(true, $"已存在，跳过：{Path.GetFileName(target)}");
                return new DownloadResult(true, target, "skipped", "文件已存在，已跳过", sw.ElapsedMilliseconds);
            }
        }

        // ---- 先写临时文件，成功后再落位 ----
        // 原因：直接写目标路径时，下载中途失败/取消会把原来那个完好的文件
        // 变成半个（甚至 0 字节）。落位用 File.Move(overwrite) 一步完成，
        // 失败时原文件完好无损。
        var temp = target + ".part";
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

            var buffer = new byte[81920];
            long done = 0;
            double lastReported = -1;
            int read;

            // 单独一个作用域：写句柄必须在**读临时文件做校验之前**关掉，
            // 否则文件仍被占用（Windows 上直接 SharingViolation，macOS 上偶发）。
            await using (var dst = File.Create(temp))
            {
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
            }

            // 校验通过再落位；不通过则报错并保留临时文件之外的原文件。
            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
            {
                var actual = DownloadFile.ComputeSha256(temp);
                if (!string.Equals(actual, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    sw.Stop();
                    TryDelete(temp);
                    return new DownloadResult(false, null, "checksum_mismatch",
                        $"校验失败：期望 {request.ExpectedSha256}，实际 {actual}", sw.ElapsedMilliseconds);
                }
            }

            // 落位：先删旧文件再 Move（Windows 上 Move 到已存在文件会失败）
            TryDelete(target);
            File.Move(temp, target);

            sw.Stop();
            return new DownloadResult(true, target, null, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            TryDelete(temp);
            return new DownloadResult(false, null, "canceled", "下载已取消", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            TryDelete(temp);
            return new DownloadResult(false, null, "download_failed", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }
}
