using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Logging;
using UMango.Core.Network;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 把多个版本源串成降级链：逐个尝试，第一个成功的胜出。
///
/// **对应 PCL-CE 的 <c>UpdatesWrapperModel</c>**（受限许可目录，只借鉴架构）。
///
/// 两条设计决定：
/// ① **失败不是异常**：某个源挂掉（网络不通、被墙、源下线）是多源架构的常态，
///    链子应该记一笔然后试下一个，而不是把异常抛给界面。
/// ② **成功后不再问后面的源**：避免每次检查都打一串无用请求。
/// </summary>
public sealed class UpdateChannelChain
{
    private readonly List<IUpdateSource> _sources = new();

    /// <summary>参与降级的源（按优先级排列）。</summary>
    public IReadOnlyList<IUpdateSource> Sources => _sources;

    /// <summary>本机不可用的源会被跳过 —— 记下来是为了让界面能说清"为什么只试了一个源"。</summary>
    public List<string> SkippedSources { get; } = new();

    /// <summary>依次失败的源及其原因。</summary>
    public List<(string Source, UpdateCheckFailure Failure)> FailedSources { get; } = new();

    /// <summary>胜出的源名（全部失败时为 null）。</summary>
    public string? WinnerSource { get; private set; }

    public UpdateChannelChain Add(IUpdateSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        _sources.Add(source);
        return this;
    }

    public UpdateChannelChain AddRange(IEnumerable<IUpdateSource> sources)
    {
        foreach (var s in sources) Add(s);
        return this;
    }

    /// <summary>
    /// 依次尝试各源，返回第一个成功的结果。
    /// </summary>
    /// <returns>
    /// 成功返回 <see cref="UpdateSourceResult"/>（其 <see cref="UpdateSourceResult.Info"/> 非空）；
    /// 全部失败时返回一个 <see cref="UpdateSourceResult"/>，其 <see cref="UpdateSourceResult.Failure"/>
    /// 描述**最后一个**源的失败原因（前面的在 <see cref="FailedSources"/> 里）。
    /// </returns>
    public async Task<UpdateSourceResult> ResolveAsync(
        UpdateChannelKind channel,
        string localVersion,
        CancellationToken ct = default)
    {
        SkippedSources.Clear();
        FailedSources.Clear();
        WinnerSource = null;

        UpdateSourceResult? last = null;
        foreach (var source in _sources)
        {
            if (!source.IsAvailable())
            {
                SkippedSources.Add(source.Name);
                continue;
            }

            // 取消检查放在每个源之前：源自己未必理会取消令牌
            // （第三方源、插件源），链子必须自己在源之间停下来，
            // 否则「用户点了取消」之后还会继续问剩下的源。
            ct.ThrowIfCancellationRequested();

            UpdateSourceResult result;
            try
            {
                result = await source.FetchAsync(channel, localVersion, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 源自己没处理好异常（第三方源、插件源可能这样）——
                // 链子不能因为一个源崩了就不检查更新了。
                result = UpdateSourceResult.Failed(
                    UpdateCheckError.Network, $"版本源 {source.Name} 抛出异常：{ex.Message}", ex);
            }

            if (result.IsSuccess)
            {
                WinnerSource = source.Name;
                return result;
            }

            FailedSources.Add((source.Name, result.Failure!));
            last = result;
        }

        if (last is not null) return last;

        // 所有源都被跳过了：这是"本机没有可用源"，与"源挂了"是不同的原因
        return UpdateSourceResult.Failed(
            UpdateCheckError.Network,
            SkippedSources.Count > 0
                ? $"本机没有可用的版本源（已跳过：{string.Join("、", SkippedSources)}）"
                : "没有配置任何版本源");
    }
}

/// <summary>
/// 基于 HTTP 的版本源：从一个 GitHub Releases 风格的地址拉取最新版本。
///
/// 这是 UMango 当前的**主源**。它内部复用 <see cref="UpdateChecker"/>，
/// 所以不重复实现 JSON 解析。
/// </summary>
public sealed class HttpUpdateSource : IUpdateSource
{
    private readonly IHttpService _http;
    private readonly string _feedUrl;
    private readonly Func<string, bool> _assetMatcher;

    public HttpUpdateSource(string name, IHttpService http, string feedUrl, Func<string, bool> assetMatcher)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("源名不能为空", nameof(name));
        if (string.IsNullOrWhiteSpace(feedUrl)) throw new ArgumentException("Feed 地址不能为空", nameof(feedUrl));

        Name = name;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _feedUrl = feedUrl;
        _assetMatcher = assetMatcher ?? throw new ArgumentNullException(nameof(assetMatcher));
    }

    public string Name { get; }

    public string FeedUrl => _feedUrl;

    public bool IsAvailable() => true;

    public async Task<UpdateSourceResult> FetchAsync(
        UpdateChannelKind channel,
        string localVersion,
        CancellationToken ct = default)
    {
        var checker = new UpdateChecker(_http, _assetMatcher);
        var outcome = await checker
            .CheckAsync(new UpdateCheckRequest(localVersion, _feedUrl, channel.AcceptsPrerelease()), ct)
            .ConfigureAwait(false);

        return outcome switch
        {
            UpdateInfo info => UpdateSourceResult.Success(info),
            UpdateCheckFailure failure => UpdateSourceResult.Failed(failure),
            _ => UpdateSourceResult.Failed(UpdateCheckError.BadPayload, "版本检查返回了未知结果")
        };
    }
}

/// <summary>
/// 静态清单源：从一个"本地写死或配置好的版本清单"里读最新版本。
///
/// **用途**：官方源（GitHub）被墙或不可达时的兜底。
/// 清单格式（极简 JSON）：
/// <code>
/// { "version": "1.2.3", "url": "https://…/UMango-1.2.3.pkg", "notes": "…" }
/// </code>
/// </summary>
public sealed class StaticManifestUpdateSource : IUpdateSource
{
    private readonly Func<CancellationToken, Task<string?>> _fetchManifest;
    private readonly Func<string, bool> _assetMatcher;

    public StaticManifestUpdateSource(
        string name,
        Func<CancellationToken, Task<string?>> fetchManifest,
        Func<string, bool> assetMatcher)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("源名不能为空", nameof(name));
        Name = name;
        _fetchManifest = fetchManifest ?? throw new ArgumentNullException(nameof(fetchManifest));
        _assetMatcher = assetMatcher ?? throw new ArgumentNullException(nameof(assetMatcher));
    }

    public string Name { get; }

    public bool IsAvailable() => true;

    public async Task<UpdateSourceResult> FetchAsync(
        UpdateChannelKind channel,
        string localVersion,
        CancellationToken ct = default)
    {
        string? manifest;
        try
        {
            manifest = await _fetchManifest(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateSourceResult.Failed(
                UpdateCheckError.Network, $"无法获取版本清单：{ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(manifest))
            return UpdateSourceResult.Failed(UpdateCheckError.Network, "版本清单为空");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(manifest);
        }
        catch (Exception ex)
        {
            return UpdateSourceResult.Failed(
                UpdateCheckError.BadPayload, "版本清单不是合法的 JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Long(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

            var versionText = Str("version");
            if (string.IsNullOrWhiteSpace(versionText))
                return UpdateSourceResult.Failed(UpdateCheckError.BadPayload, "版本清单里没有 version 字段");

            var remote = Utils.SemVer.TryParse(versionText.TrimStart('v', 'V'));
            if (remote is null)
                return UpdateSourceResult.Failed(UpdateCheckError.BadVersion, $"版本清单里的版本号无法解析：{versionText}");

            if (channel == UpdateChannelKind.Stable && remote.IsPreRelease)
                return UpdateSourceResult.Failed(
                    UpdateCheckError.BadVersion, $"版本清单指向的是预发布版 {versionText}，但当前是正式通道");

            var url = Str("url");
            if (string.IsNullOrWhiteSpace(url))
                return UpdateSourceResult.Failed(UpdateCheckError.BadPayload, "版本清单里没有 url 字段");

            if (!_assetMatcher(url!))
                return UpdateSourceResult.Failed(
                    UpdateCheckError.NoAsset, "版本清单里的安装包不匹配本平台");

            var local = Utils.SemVer.TryParse(localVersion);
            if (local is null)
                return UpdateSourceResult.Failed(UpdateCheckError.BadVersion, $"本地版本号无法解析：{localVersion}");

            var isPreRelease = remote.IsPreRelease;
            var size = Long("size");

            return UpdateSourceResult.Success(
                new UpdateInfo(remote, local, url!, size, Str("notes"), isPreRelease));
        }
    }
}
