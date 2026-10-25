using UMango.Core.Announcements;
using UMango.Shell.Services;

namespace UMango.Shell.Services;

/// <summary>
/// 公告的 App 侧接入点。
///
/// 与 <see cref="HomepageHost"/> 是同一套分工：
///   · Core（<see cref="AnnouncementService"/>）负责抓取、解析、已读记录、动作白名单判定；
///   · App 负责把"动作名"接到真正的界面行为上（跳页、开链接、刷新）。
///
/// **安全边界**：公告是远程内容，它写的 <c>command</c> 只有在
/// <see cref="AnnouncementActionRegistry"/> 里注册过才可能被执行。
/// 这里**不另存一张动作表** —— 实现直接挂在 Core 的描述符上，
/// 注册表因此是唯一真相，不会出现"Core 说允许、这里找不到实现"的静默故障。
/// </summary>
public sealed class AnnouncementHost
{
    /// <summary>公告服务（Core）。</summary>
    public AnnouncementService Service { get; }

    /// <summary>创建接入点。</summary>
    public AnnouncementHost(AnnouncementService service)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));

        // 动作实现挂到 Core 的描述符上 —— 注册与实现是同一次调用，不会分叉。
        Service.Actions
            .Register(new AnnouncementActionDescriptor("open-downloads", "打开下载页", handler: _ => Navigate("downloads")))
            .Register(new AnnouncementActionDescriptor("open-settings", "打开设置页", handler: _ => Navigate("settings")))
            .Register(new AnnouncementActionDescriptor("open-logs", "打开日志页", handler: _ => Navigate("logs")))
            .Register(new AnnouncementActionDescriptor("open-tools", "打开工具页", handler: _ => Navigate("tools")))
            .Register(new AnnouncementActionDescriptor("open-homepage", "打开自定义主页", handler: _ => Navigate("homepage")));
    }

    /// <summary>请求跳到某个页面（由 MainWindow 订阅处理）。</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>抓取公告。</summary>
    public Task<AnnouncementFetchResult> LoadAsync(CancellationToken ct = default)
        => Service.LoadAsync(ct);

    /// <summary>把一批公告标记为已展示。</summary>
    public void MarkShown(IEnumerable<Announcement> announcements) => Service.MarkShown(announcements);

    /// <summary>清空已展示记录（用户想重看一遍时用）。</summary>
    public void ResetShown() => Service.ResetShown();

    /// <summary>
    /// 执行公告上的一个按钮。
    /// 返回 false 表示动作名未知或未注册 —— **不执行任何东西**。
    /// </summary>
    public bool InvokeAction(string name, string? argument)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // 唯一真相：Core 的注册表。既判"允不允许"，也取"怎么做"。
        var descriptor = Service.Actions.Find(name);
        if (descriptor?.Handler is null) return false;

        descriptor.Handler(argument);
        return true;
    }

    private void Navigate(string target) => NavigationRequested?.Invoke(target);
}
