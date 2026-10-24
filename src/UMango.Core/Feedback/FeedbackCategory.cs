namespace UMango.Core.Feedback;

/// <summary>
/// 反馈的处理状态分类。
///
/// 对应 PCL 的 <c>PageSetupFeedback.TagId</c>（受限许可目录 → 只借鉴语义）。
///
/// PCL 用的是 **GitHub label 的数字 id**（<c>6820804544</c> 之类）—— 那是 PCL 自己仓库里
/// 那些 label 的 id，写死在代码里。别人换个仓库，这些 id 就不存在了。
/// 因此 UMango **按名字匹配**（<see cref="FeedbackCategory"/> 的 <see cref="Key"/>），
/// 并且允许通过 <see cref="FeedbackTagMap"/> 追加自定义分类。
///
/// 顺序即界面显示顺序（与 PCL 的 TagMap 一致：处理中 → 等待处理 → 等待 → 暂停 →
/// 下一个 → 已完成 → 已拒绝 → 已忽略 → 重复）。
/// </summary>
public enum FeedbackCategory
{
    /// <summary>
    /// 未分类（没有任何已知标签）。
    ///
    /// **必须是 0 号成员** —— `Enum.TryParse` 失败时会把 out 参数置为
    /// `default(T)`，也就是 0 号成员。若 0 号是别的分类，
    /// 「解析失败」就会静默变成「那个分类」。这个坑在测试里被抓到过。
    /// </summary>
    Uncategorized = 0,

    /// <summary>处理中。</summary>
    Processing,

    /// <summary>等待处理。</summary>
    WaitingProcess,

    /// <summary>等待（作者回复后待确认）。</summary>
    Wait,

    /// <summary>暂停。</summary>
    Pause,

    /// <summary>下一个待处理。</summary>
    Upnext,

    /// <summary>已完成。</summary>
    Completed,

    /// <summary>已拒绝。</summary>
    Decline,

    /// <summary>已忽略。</summary>
    Ignored,

    /// <summary>重复。</summary>
    Duplicate
}

/// <summary>
/// 标签 → 分类的映射表。
///
/// PCL 用数字 label id 硬编码在 <c>TagMap</c> 字典里；UMango 改成
/// **按 label 名字匹配**（大小写不敏感），并允许调用方注册额外映射。
/// </summary>
public sealed class FeedbackTagMap
{
    private readonly Dictionary<string, FeedbackCategory> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FeedbackCategory> _order = [];

    /// <summary>构造一个带默认映射的表。</summary>
    public FeedbackTagMap()
    {
        Register("processing", FeedbackCategory.Processing);
        Register("处理中", FeedbackCategory.Processing);
        Register("等待处理", FeedbackCategory.WaitingProcess);
        Register("waiting-process", FeedbackCategory.WaitingProcess);
        Register("waiting for process", FeedbackCategory.WaitingProcess);
        Register("wait", FeedbackCategory.Wait);
        Register("等待", FeedbackCategory.Wait);
        Register("pause", FeedbackCategory.Pause);
        Register("暂停", FeedbackCategory.Pause);
        Register("upnext", FeedbackCategory.Upnext);
        Register("up next", FeedbackCategory.Upnext);
        Register("下一个", FeedbackCategory.Upnext);
        Register("completed", FeedbackCategory.Completed);
        Register("done", FeedbackCategory.Completed);
        Register("已完成", FeedbackCategory.Completed);
        Register("decline", FeedbackCategory.Decline);
        Register("declined", FeedbackCategory.Decline);
        Register("已拒绝", FeedbackCategory.Decline);
        Register("ignored", FeedbackCategory.Ignored);
        Register("已忽略", FeedbackCategory.Ignored);
        Register("duplicate", FeedbackCategory.Duplicate);
        Register("重复", FeedbackCategory.Duplicate);
    }

    /// <summary>
    /// 注册一个标签名 → 分类的映射。
    /// </summary>
    /// <param name="label">标签名（大小写不敏感）。</param>
    /// <param name="category">目标分类。</param>
    public void Register(string label, FeedbackCategory category)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        _map[label.Trim()] = category;
        if (!_order.Contains(category)) _order.Add(category);
    }

    /// <summary>
    /// 从一条反馈的标签里找出它属于哪个分类。
    ///
    /// 规则（与 PCL 一致，但更明确）：
    ///   ① 先看顶层 <c>type.name</c> —— 那是显式声明的分类，优先级最高；
    ///   ② 再按标签名逐个匹配（取第一个能匹配上的）；
    ///   ③ 都没有 → <see cref="FeedbackCategory.Uncategorized"/>。
    ///
    /// **PCL 的做法是反过来的**：它先按数字 label id 找面板，找不到就用
    /// <c>type.name</c> 兜底。UMango 之所以反过来，是因为 PCL 那个 `type.name`
    /// 只是个**显示用的小写字符串**（如 "uncategorized"），不是分类真值；
    /// 而显式声明的 type 本来就该说话算数。
    /// </summary>
    public FeedbackCategory Classify(FeedbackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var typeName = entry.Type?.Name;
        if (!string.IsNullOrWhiteSpace(typeName) && TryParseCategory(typeName, out var byType))
            return byType;

        foreach (var label in entry.Labels)
        {
            var key = label.Name ?? label.Id;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (_map.TryGetValue(key.Trim(), out var category)) return category;
        }

        return FeedbackCategory.Uncategorized;
    }

    /// <summary>
    /// 把分类名（英文名或中文名）解析成枚举值。
    /// </summary>
    public static bool TryParseCategory(string name, out FeedbackCategory category)
    {
        category = FeedbackCategory.Uncategorized;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        if (Enum.TryParse<FeedbackCategory>(trimmed, ignoreCase: true, out category)) return true;

        // 中文名
        switch (trimmed)
        {
            case "处理中": category = FeedbackCategory.Processing; return true;
            case "等待处理": category = FeedbackCategory.WaitingProcess; return true;
            case "等待": category = FeedbackCategory.Wait; return true;
            case "暂停": category = FeedbackCategory.Pause; return true;
            case "下一个": category = FeedbackCategory.Upnext; return true;
            case "已完成": category = FeedbackCategory.Completed; return true;
            case "已拒绝": category = FeedbackCategory.Decline; return true;
            case "已忽略": category = FeedbackCategory.Ignored; return true;
            case "重复": category = FeedbackCategory.Duplicate; return true;
            case "未分类": category = FeedbackCategory.Uncategorized; return true;
            default: return false;
        }
    }

    /// <summary>界面显示顺序（只含有条目的分类）。</summary>
    public IReadOnlyList<FeedbackCategory> Order => _order;
}
