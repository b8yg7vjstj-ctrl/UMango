using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Network;
using UMango.Core.Utils;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 版本检查器：从远端 Feed 拉取最新版本并与本地比较。
///
/// **Feed 格式**：GitHub Releases API 的 JSON（<c>api.github.com/repos/{owner}/{repo}/releases/latest</c>）
/// 或任意兼容该结构的地址。选它的理由：
/// ① GitHub 是 PCL-CE 与 UMango 的实际发布渠道；
/// ② 结构里自带 <c>assets</c> 下载地址与体积，无需自定义协议；
/// ③ PCL-CE 的 Minio/MirrorChyan 多通道源（<c>Modules/Updates/</c>，受限层）
///    与其官方服务配套，对 UMango 无意义，不迁移。
///
/// **平台相关的安装包筛选**：一个 release 通常同时发布 win-x64/win-arm64/osx-arm64/linux-x64
/// 多个文件。用 <paramref name="AssetMatcher"/>（由调用方按 RID 提供）挑对的那个。
/// </summary>
public sealed class UpdateChecker
{
    private readonly IHttpService _http;
    private readonly Func<string, bool> _assetMatcher;

    public UpdateChecker(IHttpService http, Func<string, bool> assetMatcher)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _assetMatcher = assetMatcher ?? throw new ArgumentNullException(nameof(assetMatcher));
    }

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <returns>
    /// 成功时返回 <see cref="UpdateInfo"/>（即使 <see cref="UpdateInfo.IsNewer"/> 为 false，
    /// 也表示「已是最新」）；失败时返回 <see cref="UpdateCheckFailure"/>。
    /// 用 <c>is UpdateCheckFailure</c> 判断走哪条分支。
    /// </returns>
    public async Task<object> CheckAsync(UpdateCheckRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // 用 TryParse 而不是 new SemVer：后者的宽容构造器会把 "not-a-version" 这类
        // 垃圾串静默解析成 0.0.0（测试实际发现的缺陷），那会让"永远有新版"或"永远没新版"。
        var local = SemVer.TryParse(request.LocalVersion);
        if (local is null)
        {
            return new UpdateCheckFailure(UpdateCheckError.BadVersion,
                $"本地版本号无法解析：{request.LocalVersion}");
        }

        HttpResponse<string> raw;
        try
        {
            raw = await _http.GetStringAsync(request.FeedUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckFailure(UpdateCheckError.Network,
                $"无法访问版本信息源：{ex.Message}", ex);
        }

        if (!raw.IsSuccess || raw.Value is null)
        {
            return new UpdateCheckFailure(UpdateCheckError.Network,
                $"版本信息源返回 {raw.StatusCode}：{raw.Error ?? "无详细信息"}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw.Value);
        }
        catch (JsonException ex)
        {
            return new UpdateCheckFailure(UpdateCheckError.BadPayload,
                "版本信息不是合法的 JSON（可能返回了错误页）", ex);
        }

        using (doc)
        {
            var root = doc.RootElement.Clone();
            var tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
                return new UpdateCheckFailure(UpdateCheckError.BadPayload, "版本信息里没有 tag_name 字段");

            var versionText = tagName.TrimStart('v', 'V');
            var remote = SemVer.TryParse(versionText);
            if (remote is null)
            {
                return new UpdateCheckFailure(UpdateCheckError.BadVersion,
                    $"远端版本号无法解析：{tagName}");
            }

            // GitHub 的 prerelease 布尔字段才是权威：tag 不带 -beta 也可能被标为预发布
            // （CI 产物、热修通道等）。SemVer 的 -prerelease 标记只覆盖 tag 自带后缀的情况，
            // 两者取或，缺一会漏判（测试实际发现的缺陷）。
            var isPreRelease = remote.IsPreRelease ||
                               (root.TryGetProperty("prerelease", out var pre) &&
                                pre.ValueKind == JsonValueKind.True);

            if (isPreRelease && !request.IncludePreRelease)
            {
                return new UpdateCheckFailure(UpdateCheckError.BadVersion,
                    $"远端最新版本 {tagName} 是预发布版，但本次检查不接受预发布");
            }

            string? downloadUrl = null;
            long? size = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !_assetMatcher(name)) continue;
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                return new UpdateCheckFailure(UpdateCheckError.NoAsset,
                    "远端版本没有匹配本平台的安装包资源");

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            return new UpdateInfo(remote, local, downloadUrl!, size, notes, isPreRelease);
        }
    }
}
