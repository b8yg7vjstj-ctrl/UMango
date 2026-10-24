using UMango.Core.Network;

namespace UMango.Core.Feedback;

/// <summary>抓取结果状态。</summary>
public enum FeedbackFetchStatus
{
    /// <summary>成功。</summary>
    Ok,

    /// <summary>失败（网络 / 解析）。</summary>
    Failed,

    /// <summary>没有配置源。</summary>
    NoSource
}

/// <summary>反馈抓取结果。</summary>
public sealed class FeedbackFetchResult
{
    /// <summary>状态。</summary>
    public FeedbackFetchStatus Status { get; init; }

    /// <summary>反馈条目（已按分类分组，组内保持抓取顺序）。</summary>
    public IReadOnlyList<FeedbackEntry> Items { get; init; } = [];

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>是否成功。</summary>
    public bool IsSuccess => Status == FeedbackFetchStatus.Ok;
}

/// <summary>反馈页需要的诊断信息（拼装给用户填进 issue 正文）。</summary>
public sealed class FeedbackDiagnosticInfo
{
    /// <summary>标题行。</summary>
    public string Title { get; init; } = "";

    /// <summary>正文（Markdown 源码）。</summary>
    public string Content { get; init; } = "";

    /// <summary>系统/版本等逐行信息。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
}

/// <summary>
/// 反馈服务：抓取反馈列表、分组、并拼装提交反馈时要预填的诊断信息。
///
/// 对应 PCL 的 <c>PageSetupFeedback</c>（受限许可目录 → 只借鉴语义，重新实现）。
/// </summary>
public sealed class FeedbackService
{
    private readonly IHttpService _http;
    private readonly FeedbackTagMap _tags;
    private readonly Func<FeedbackDiagnosticInfo> _diagnostics;

    /// <summary>构造服务。</summary>
    /// <param name="http">HTTP 客户端。</param>
    /// <param name="tags">标签 → 分类映射表（可自定义）。</param>
    /// <param name="diagnostics">诊断信息提供者（用于拼装 issue 正文）。</param>
    public FeedbackService(IHttpService http, FeedbackTagMap? tags = null,
        Func<FeedbackDiagnosticInfo>? diagnostics = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tags = tags ?? new FeedbackTagMap();
        _diagnostics = diagnostics ?? (() => new FeedbackDiagnosticInfo());
    }

    /// <summary>标签映射表（只读视图）。</summary>
    public FeedbackTagMap Tags => _tags;

    /// <summary>
    /// 从源抓取反馈列表。
    ///
    /// 源为空 → <see cref="FeedbackFetchStatus.NoSource"/>（不是失败）。
    /// 源返回的 JSON 形状须为 <c>{ "items": [ … ] }</c>（GitHub Issues API 形状）。
    /// </summary>
    public async Task<FeedbackFetchResult> FetchAsync(string? source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new FeedbackFetchResult { Status = FeedbackFetchStatus.NoSource };

        var response = await _http.GetJsonAsync<FeedbackList>(source, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Value is null)
            return new FeedbackFetchResult
            {
                Status = FeedbackFetchStatus.Failed,
                Error = response.Error ?? "获取反馈失败"
            };

        var items = response.Value.Items ?? [];
        // 过滤掉既没有分类依据也没有正文的条目 —— 它们在界面上是纯噪音。
        var usable = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Title) || !string.IsNullOrWhiteSpace(i.Content))
            .ToList();

        return new FeedbackFetchResult
        {
            Status = FeedbackFetchStatus.Ok,
            Items = usable
        };
    }

    /// <summary>
    /// 把反馈列表按分类分组，组内保持原顺序，组间按 <see cref="FeedbackTagMap.Order"/>。
    /// </summary>
    public IReadOnlyList<FeedbackGroup> Group(IEnumerable<FeedbackEntry> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var groups = new List<FeedbackGroup>();
        var byCategory = new Dictionary<FeedbackCategory, List<FeedbackEntry>>();

        foreach (var item in items)
        {
            var category = _tags.Classify(item);
            if (!byCategory.TryGetValue(category, out var list))
            {
                list = [];
                byCategory[category] = list;
            }
            list.Add(item);
        }

        // 有条目的分类按注册顺序排在前，未分类永远最后。
        foreach (var category in _tags.Order)
        {
            if (byCategory.TryGetValue(category, out var list))
                groups.Add(new FeedbackGroup(category, list));
        }
        if (byCategory.TryGetValue(FeedbackCategory.Uncategorized, out var rest))
            groups.Add(new FeedbackGroup(FeedbackCategory.Uncategorized, rest));

        return groups;
    }

    /// <summary>
    /// 拼装提交反馈时预填的诊断信息（PCL 的 <c>FeedbackInfo</c> 语义）。
    ///
    /// 标题固定为「[反馈] 」，正文包含系统信息行。
    /// </summary>
    public FeedbackDiagnosticInfo BuildDiagnosticInfo()
    {
        var info = _diagnostics();
        var content = string.IsNullOrWhiteSpace(info.Content)
            ? string.Join("\n", info.Lines)
            : info.Content;

        return new FeedbackDiagnosticInfo
        {
            Title = string.IsNullOrWhiteSpace(info.Title) ? "[反馈] " : info.Title,
            Content = content,
            Lines = info.Lines
        };
    }
}

/// <summary>一组同类反馈。</summary>
public sealed class FeedbackGroup
{
    /// <summary>构造一组。</summary>
    public FeedbackGroup(FeedbackCategory category, IReadOnlyList<FeedbackEntry> items)
    {
        Category = category;
        Items = items;
    }

    /// <summary>分类。</summary>
    public FeedbackCategory Category { get; }

    /// <summary>组内条目。</summary>
    public IReadOnlyList<FeedbackEntry> Items { get; }
}
