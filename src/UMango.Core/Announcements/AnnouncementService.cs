using System.Text.Json;
using UMango.Core.Configuration;
using UMango.Core.Network;

namespace UMango.Core.Announcements;

/// <summary>
/// 公告（主页新闻）服务。
///
/// 对应 PCL 的 `AnnouncementService`（`Plain Craft Launcher 2/Modules/Updates/AnnouncementService.cs`，
/// 位于**受限许可目录** → 只借鉴语义，重新实现）。
///
/// PCL 的机制本身与它的官方服务无关，可以照搬的部分有三条：
///   ① 按 id 记住"已展示过的公告"，重启后不再重复弹；
///   ② 一次可能返回多条，逐条展示；
///   ③ 每条公告可带两个按钮，按钮触发一个**具名动作**。
///
/// 与 PCL 的实质差异：
///   ① **数据源可配置**（PCL 写死它的官网 API；UMango 用别人的公告源没意义）——
///      没有配置源就什么都不做，不去访问任何默认地址。
///   ② 公告内容**不直接渲染成富文本/可执行内容** —— 按钮动作必须走
///      <see cref="AnnouncementActionRegistry"/> 里的白名单，未注册的具名动作被拒绝。
///      （PCL 那边靠 `XamlEventSanitizer` 黑名单过滤远程 XAML；白名单比黑名单可靠。）
///   ③ 抓取失败只记日志，不弹窗 —— 公告不是关键路径，不该打断用户。
/// </summary>
public sealed class AnnouncementService
{
    /// <summary>日志分类名。</summary>
    public const string ModuleName = "Announcement";

    /// <summary>已展示公告的 id 存在设置里的键名。</summary>
    public const string ShowedKey = UiSettings.AnnouncementShowedKey;

    /// <summary>公告源地址在设置里的键名（与设置页的描述符同一个键）。</summary>
    public const string SourceKey = UiSettings.AnnouncementSourceKey;

    /// <summary>最多记住多少条"已展示"记录（防止设置无限膨胀）。</summary>
    public const int MaxRemembered = 200;

    private readonly IHttpService _http;
    private readonly SettingsService _settings;

    /// <summary>构造。</summary>
    public AnnouncementService(IHttpService http, SettingsService settings)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>允许触发的公告按钮动作。默认是空的 —— 什么都不注册就什么都不许触发。</summary>
    public AnnouncementActionRegistry Actions { get; } = new();

    /// <summary>最近一次抓取的结果（供界面显示"已是最新"之类）。</summary>
    public AnnouncementFetchResult? LastResult { get; private set; }

    /// <summary>
    /// 抓取公告列表，**并筛掉已经展示过的**。
    ///
    /// 没有配置源 → 返回"没有源"，不发起任何网络请求。
    /// </summary>
    public async Task<AnnouncementFetchResult> LoadAsync(CancellationToken ct = default)
    {
        var source = _settings.Get<string>(SourceKey);
        if (string.IsNullOrWhiteSpace(source))
        {
            LastResult = AnnouncementFetchResult.NoSource();
            return LastResult;
        }

        HttpResponse<AnnouncementList> response;
        try
        {
            response = await _http.GetJsonAsync<AnnouncementList>(source, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastResult = AnnouncementFetchResult.Failed("抓取公告失败：" + ex.Message);
            return LastResult;
        }

        if (!response.IsSuccess || response.Value is null)
        {
            LastResult = AnnouncementFetchResult.Failed(response.Error ?? "公告源返回了空结果");
            return LastResult;
        }

        var showed = ReadShowed();
        var fresh = response.Value.Content
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .Where(a => !showed.Contains(a.Id))
            .ToList();

        LastResult = AnnouncementFetchResult.Succeeded(fresh, response.Value.Content.Count);
        return LastResult;
    }

    /// <summary>
    /// 展示完一批公告后把它们记进"已展示"。
    /// </summary>
    public void MarkShown(IEnumerable<Announcement> announcements)
    {
        var showed = ReadShowed();
        foreach (var a in announcements)
        {
            if (!string.IsNullOrWhiteSpace(a.Id)) showed.Add(a.Id);
        }

        // 超过上限就丢最旧的 —— 公告的"已读"记录只需要最近的一批
        if (showed.Count > MaxRemembered)
        {
            showed = [.. showed.Skip(showed.Count - MaxRemembered)];
        }

        _settings.Set(ShowedKey, string.Join('|', showed));
        _settings.Save();
    }

    /// <summary>读出"已展示过的公告 id"集合。</summary>
    public HashSet<string> ReadShowed()
    {
        var raw = _settings.Get<string>(ShowedKey);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    /// <summary>清空"已展示"记录（用户想重新看一遍时用）。</summary>
    public void ResetShown()
    {
        _settings.Set(ShowedKey, "");
        _settings.Save();
    }
}

/// <summary>公告动作的描述符。</summary>
public sealed class AnnouncementActionDescriptor
{
    /// <summary>构造。</summary>
    public AnnouncementActionDescriptor(
        string name,
        string description,
        bool requiresConfirmation = false,
        Action<string?>? handler = null)
    {
        Name = name;
        Description = description;
        RequiresConfirmation = requiresConfirmation;
        Handler = handler;
    }

    /// <summary>动作名（与公告 JSON 里的 <c>command</c> 对应）。</summary>
    public string Name { get; }

    /// <summary>动作说明（给用户看）。</summary>
    public string Description { get; }

    /// <summary>是否需要二次确认。</summary>
    public bool RequiresConfirmation { get; }

    /// <summary>
    /// 动作的实现（由界面层注册：跳页 / 开链接 / 刷新）。
    ///
    /// **为什么处理器放在描述符里，而不是界面层另存一张表**：
    /// 两张表意味着"注册表说允许、但实现表里没有"这种对不上的状态，
    /// 判定逻辑要同时问两张表才安全 —— 而两张表一旦分叉，
    /// 就会出现"功能看起来正常、就是不触发"的静默故障（主页清单那边已经踩过这个坑）。
    /// 把实现挂在描述符上，注册表就成了**唯一真相**。
    ///
    /// 为 null 表示"登记了名字但还没人实现" —— 此时点击不做任何事。
    /// </summary>
    public Action<string?>? Handler { get; }
}

/// <summary>
/// 公告按钮动作的白名单。
///
/// 只有注册在这里的动作才允许被公告触发 —— 公告是**远程内容**，
/// 让它直接执行任意命令等于给远程代码执行开了口子。
/// </summary>
public sealed class AnnouncementActionRegistry
{
    // 动作名匹配**忽略大小写**，与主页清单的注册表（HomepageActionRegistry）保持一致。
    // 同一套"具名动作"用两套大小写规则，会出现"主页里能触发、公告里触发不了"的诡异不一致。
    // 安全性不靠大小写 —— 靠的是"必须先注册"这条白名单本身。
    private readonly Dictionary<string, AnnouncementActionDescriptor> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>注册一个动作。</summary>
    public AnnouncementActionRegistry Register(AnnouncementActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_lock) _actions[descriptor.Name] = descriptor;
        return this;
    }

    /// <summary>某个动作是否已注册。</summary>
    public bool IsRegistered(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock) return _actions.ContainsKey(name);
    }

    /// <summary>查找动作。</summary>
    public AnnouncementActionDescriptor? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock) return _actions.GetValueOrDefault(name);
    }

    /// <summary>全部已注册动作。</summary>
    public IReadOnlyCollection<AnnouncementActionDescriptor> All
    {
        get { lock (_lock) return _actions.Values.ToList(); }
    }
}

/// <summary>抓取公告的结果。</summary>
public sealed class AnnouncementFetchResult
{
    private AnnouncementFetchResult(AnnouncementFetchStatus status, List<Announcement> items, int total, string? error)
    {
        Status = status;
        Items = items;
        TotalCount = total;
        Error = error;
    }

    /// <summary>结果状态。</summary>
    public AnnouncementFetchStatus Status { get; }

    /// <summary>还没展示过的公告。</summary>
    public List<Announcement> Items { get; }

    /// <summary>源里一共返回了多少条。</summary>
    public int TotalCount { get; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; }

    /// <summary>成功且有新公告。</summary>
    public bool HasNew => Status == AnnouncementFetchStatus.Ok && Items.Count > 0;

    internal static AnnouncementFetchResult Succeeded(List<Announcement> items, int total)
        => new(AnnouncementFetchStatus.Ok, items, total, null);

    internal static AnnouncementFetchResult Failed(string error)
        => new(AnnouncementFetchStatus.Failed, [], 0, error);

    internal static AnnouncementFetchResult NoSource()
        => new(AnnouncementFetchStatus.NoSource, [], 0, null);
}

/// <summary>抓取公告的状态。</summary>
public enum AnnouncementFetchStatus
{
    /// <summary>成功。</summary>
    Ok,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>没有配置公告源。</summary>
    NoSource,
}
