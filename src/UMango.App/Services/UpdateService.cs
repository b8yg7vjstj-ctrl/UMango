using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Essentials;
using UMango.Core.Logging;
using UMango.Core.Network;
using UMango.Core.Platform;

namespace UMango.App.Services;

/// <summary>
/// 自更新的 App 层封装：检查 → 下载 → 交给助手进程替换。
///
/// **分工**：Core 层的 <c>UpdateChecker/UpdateCommandLine/UpdateHelper</c> 是平台中立、
/// 可单测的纯逻辑；本类负责把三者与 UMango 的实际运行环境缝在一起：
/// ① 从程序集读本地版本号；② 按 RID 挑安装包；③ 下载并汇报进度；
/// ④ 用 <see cref="UpdateCommandLine.BuildApplyArguments"/> 启动助手进程。
///
/// **Windows 与非 Windows 的差异只在最后一步**：Windows 上运行中的 exe 被锁，
/// 必须走助手进程；macOS/Linux 上 .app 目录里的文件同样可能被占用，
/// 所以流程保持一致（助手进程在两个平台上都能工作）。
/// </summary>
public sealed class UpdateService
{
    /// <summary>版本信息源（GitHub Releases 的 latest）。</summary>
    public const string FeedUrl = "https://api.github.com/repos/UMango-Community/UMango/releases/latest";

    private readonly IHttpService _http;

    public UpdateService(IHttpService http)
    {
        _http = http;
    }

    /// <summary>本地版本号（来自程序集的 InformationalVersion，取不到返回 0.0.0）。</summary>
    public static string LocalVersion
    {
        get
        {
            var asm = typeof(UpdateService).Assembly;
            var raw = asm.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            var text = raw.Length > 0
                ? ((System.Reflection.AssemblyInformationalVersionAttribute)raw[0]).InformationalVersion
                : asm.GetName().Version?.ToString() ?? "0.0.0";
            // InformationalVersion 形如 "1.2.3+githash"，SemVer 只认三段号 + 可选后缀
            var plus = text.IndexOf('+');
            return plus >= 0 ? text[..plus] : text;
        }
    }

    /// <summary>本机应下载的安装包文件名特征（与发布流水线的命名约定一致）。</summary>
    private static bool IsMyAsset(string name)
    {
        // SystemInfo 只暴露 IsWindows 与 Platform；其余平台用 PlatformKind 判等
        // （避免为一个布尔属性扩公共 API，Platform 本身才是唯一权威）。
        if (SystemInfo.IsWindows)
            return name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);
        if (SystemInfo.Platform == PlatformKind.MacOs)
            return name.Contains("osx-arm64", StringComparison.OrdinalIgnoreCase);
        return name.Contains("linux-x64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>检查更新。</summary>
    public Task<object> CheckAsync(CancellationToken ct = default)
    {
        var checker = new UpdateChecker(_http, IsMyAsset);
        return checker.CheckAsync(new UpdateCheckRequest(LocalVersion, FeedUrl), ct);
    }

    /// <summary>
    /// 下载安装包到临时目录并返回其路径（带进度回调）。
    /// </summary>
    /// <param name="url">安装包地址</param>
    /// <param name="progress">0..1，可能不单调（服务端未声明总长时只报 0/1）</param>
    public async Task<string> DownloadAsync(string url, Action<double>? progress, CancellationToken ct = default)
    {
        var target = Path.Combine(Path.GetTempPath(), $"UMango-update-{Guid.NewGuid():n}.pkg");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (total is > 0) progress?.Invoke(Math.Clamp((double)received / total.Value, 0, 1));
        }

        progress?.Invoke(1);
        Log.Info("Update", $"安装包已下载：{target}（{received} 字节）");
        return target;
    }

    /// <summary>
    /// 启动助手进程完成替换（本进程随即退出）。
    /// </summary>
    /// <returns>助手进程启动失败时返回异常；成功返回 null</returns>
    public Exception? LaunchApply(string downloadedPath, bool restart = true)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target))
            return new InvalidOperationException("取不到当前可执行文件路径，无法自更新");

        var args = UpdateCommandLine.BuildApplyArguments(
            Environment.ProcessId, target, downloadedPath, restart);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true };
            foreach (var a in args) startInfo.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
