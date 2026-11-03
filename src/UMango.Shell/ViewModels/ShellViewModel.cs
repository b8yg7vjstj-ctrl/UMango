using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.Input;
using UMango.Shell.Icons;

namespace UMango.Shell.ViewModels;

/// <summary>顶部 banner 选项卡（外壳的四个页面 + 工具）。</summary>
public enum AppTab
{
    Home,
    Download,
    Progress,
    Settings,
    Tools
}

/// <summary>
/// 左栏菜单项。
/// <paramref name="Subtitle"/> 给需要第二行说明的入口留位置（例如包源分类的安装目录），
/// 普通菜单项只填 Key/Title。
/// <paramref name="IsSecondary"/> 标记这一项是否是**次级页面**。
///
/// <param name="GroupTitle">标记这一项所属分组的标题（PCL-CE 的分组标题行
/// 字号 12、透明度 0.6、Margin="13,5,5,3"）；同一分组里只有第一项带这个值，
/// 渲染出来就是「分组标题 + 若干项」交替的左栏。</param>
///
/// 规则（对应 PCL-CE 的次级页面机制）：
///   每个选项卡的**第一项是主视图**，其余项都是次级页面。
///   进入次级页面会压栈（顶部出现「← 返回 + 页面标题」），
///   点返回会回到上一个视图，并把左栏高亮一并还原。
/// </summary>
public sealed record NavItem(
    string Key,
    string Title,
    string? Subtitle = null,
    bool IsSecondary = false,
    string? GroupTitle = null,
    IReadOnlyList<NavItemButton>? Buttons = null)
{
    /// <summary>
    /// 这一项是否要显示分组标题行。
    /// WPF 侧用 DataTrigger 判 <c>GroupTitle != null</c>；Avalonia 没有 DataTrigger，
    /// 所以两边都绑这个布尔属性，避免两套判断逻辑（曾经因此漏画过标题行）。
    /// </summary>
    public bool HasGroupTitle => !string.IsNullOrEmpty(GroupTitle);

    /// <summary>是否要显示副标题行（同上，跨壳共用的判定）。</summary>
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>是否要显示右侧快捷按钮（跨壳共用；空列表等价于没有）。</summary>
    public bool HasButtons => Buttons is { Count: > 0 };

    /// <summary>
    /// 这一项对应的 SVG 图标标识（如 <c>lucide/home</c>），由 Key 查 <see cref="NavIconMap"/> 得到。
    ///
    /// 为什么做成派生属性而不是构造参数：图标纯粹由 Key 决定，两个壳都绑这一个属性就够，
    /// 不必各自维护一份映射（那样加导航项时总有一边会忘记补图标）。
    /// 没有对应图标时返回空串，图标控件应渲染成空白而不是报错。
    /// </summary>
    public string Icon => NavIconMap.Resolve(Key);
}

/// <summary>
/// 左栏项右侧的快捷按钮（对应 PCL-CE 的 <c>MyListItem.Buttons</c> /
/// <c>MyIconButton</c>：小图标 + 悬停提示，默认只在鼠标悬停在该项时出现）。
///
/// <paramref name="Action"/> 是**动作标识**而不是命令对象：
/// Shell 层不能引用界面框架的命令类型，具体执行由各壳按标识分发
/// （WPF 在 MainWindow 的点击处理器里，Avalonia 同理）。
/// </summary>
public sealed record NavItemButton(string Action, string Icon, string ToolTip);

/// <summary>
/// 外壳 ViewModel：选项卡状态 + 随选项卡变化的左栏菜单。
///
/// 需求原话是"下方一个选项卡对应菜单"，所以 LeftMenu 不是固定列表，
/// 而是由 <see cref="CurrentTab"/> 推导出来的；页面只需绑定 <see cref="LeftMenu"/>，
/// 再用 <see cref="CurrentNavKey"/> 决定右区显示哪一块内容。
///
/// 这里不做页面实例管理（页面由 MainWindow 持有），
/// 只负责"当前选中什么"这一个真相来源，避免多处各存一份选中状态。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    /// <summary>窗口/首页标题</summary>
    public string Title { get; } = "UMango";

    [ObservableProperty]
    private AppTab _currentTab = AppTab.Home;

    private IReadOnlyList<NavItem> _leftMenu = [];

    /// <summary>当前选项卡对应的左栏菜单（随 <see cref="CurrentTab"/> 变化重建）。</summary>
    public IReadOnlyList<NavItem> LeftMenu
    {
        get => _leftMenu;
        private set => SetProperty(ref _leftMenu, value);
    }

    [ObservableProperty]
    private string _currentNavKey = "";

    /// <summary>左栏菜单被重建时触发（菜单项数量可能变化，界面需要重新生成左栏）。</summary>
    public event Action? LeftMenuChanged;

    /// <summary>左栏选中项变化时触发，参数为新的 Key。
    /// 为什么用事件而不只靠 PropertyChanged：页面切换右区内容时需要一个明确的"选中了哪一项"信号。</summary>
    public event Action<string>? NavChanged;

    /// <summary>
    /// 要隐藏的工具页入口（Key → 隐藏开关）。
    /// 对应 PCL 的「隐藏功能」机制（见 Core 的 <c>HidePolicy</c>）。
    /// </summary>
    public IReadOnlySet<string> HiddenToolKeys { get; private set; } =
        new HashSet<string>();

    /// <summary>要隐藏的顶部选项卡（<see cref="AppTab"/> 集合）。</summary>
    public IReadOnlySet<AppTab> HiddenTabs { get; private set; } =
        new HashSet<AppTab>();

    /// <summary>设置「要隐藏哪些入口」并重建菜单。</summary>
    public void ApplyHiddenEntries(
        IReadOnlySet<string> hiddenToolKeys,
        IReadOnlySet<AppTab> hiddenTabs)
    {
        HiddenToolKeys = hiddenToolKeys ?? new HashSet<string>();
        HiddenTabs = hiddenTabs ?? new HashSet<AppTab>();
        RebuildLeftMenu(CurrentTab);
    }

    public ShellViewModel()
    {
        // 构造时先建一次菜单：OnCurrentTabChanged 只在"变化"时触发，
        // 若不在构造里建，首页启动时左栏会是空的。
        RebuildLeftMenu(CurrentTab);
    }

    /// <summary>切换选项卡：重建左栏菜单并把选中项复位到该选项卡的第一项。</summary>
    partial void OnCurrentTabChanged(AppTab value) => RebuildLeftMenu(value);

    partial void OnCurrentNavKeyChanged(string value) => NavChanged?.Invoke(value);

    /// <summary>供顶部 banner 选项卡调用（也可直接双向绑定 CurrentTab）。</summary>
    [RelayCommand]
    private void SwitchTab(AppTab tab) => CurrentTab = tab;

    /// <summary>供左栏菜单项调用：CommandParameter 传 NavItem（或传 Key 字符串）。</summary>
    [RelayCommand]
    private void SelectNav(object? item)
    {
        switch (item)
        {
            case NavItem nav:
                CurrentNavKey = nav.Key;
                break;
            case string key when !string.IsNullOrWhiteSpace(key):
                CurrentNavKey = key;
                break;
        }
    }

    private void RebuildLeftMenu(AppTab tab)
    {
        var items = FilterHidden(MenuFor(tab));
        LeftMenu = items;

        // 换选项卡时选中项复位到第一项：沿用上一个选项卡的选中项会让右区内容与
        // 左栏高亮项对不上（例如从"搜索 结果"切到进度页，"搜索结果"并不存在于当前菜单）。
        CurrentNavKey = items.Length > 0 ? items[0].Key : string.Empty;
        LeftMenuChanged?.Invoke();
    }

    /// <summary>
    /// 各选项卡对应的菜单项。Key 用稳定的英文标识（页面据此切换内容），Title 是界面文案。
    /// </summary>
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    /// <summary>
    /// 各选项卡对应的菜单项。Key 用稳定的英文标识（页面据此切换内容），Title 是界面文案。
    ///
    /// 第一项是主视图，其余是次级页面（<see cref="NavItem.IsSecondary"/>）。
    /// </summary>
    private NavItem[] MenuFor(AppTab tab) => tab switch
    {
        AppTab.Home =>
        [
            new NavItem("overview", L("Nav.Overview"), GroupTitle: L("Nav.Group.Home")),
            new NavItem("records", L("Nav.Records"), IsSecondary: true)
        ],
        AppTab.Download =>
        [
            new NavItem("sources", L("Nav.Sources"), GroupTitle: L("Nav.Group.Download")),
            new NavItem("results", L("Nav.Results"), IsSecondary: true),
            new NavItem("favorites", L("Nav.Favorites"), IsSecondary: true)
        ],
        AppTab.Progress =>
        [
            new NavItem("running", L("Nav.Running"), GroupTitle: L("Nav.Group.Progress")),
            new NavItem("finished", L("Nav.Finished"), IsSecondary: true)
        ],
        AppTab.Settings =>
        [
            new NavItem("theme", L("Nav.Theme"), GroupTitle: L("Nav.Group.Settings")),
            new NavItem("debug", L("Nav.Debug"), IsSecondary: true),
            new NavItem("recordManage", L("Nav.RecordManage"), IsSecondary: true)
        ],
        _ =>
        [
            // 下载器放在工具页下，对应 PCL-CE 的「百宝箱」位置
            new NavItem("downloader", L("Nav.Downloader"), GroupTitle: L("Nav.Group.Tools")),
            new NavItem("logs", L("Nav.Logs"), IsSecondary: true,
                Buttons: [new NavItemButton("logs.openFolder", "lucide/scroll-text", L("Common.Action.OpenFolder"))]),
            new NavItem("diagnostics", L("Nav.Diagnostics"), IsSecondary: true),
            new NavItem("licenses", L("Nav.Licenses"), IsSecondary: true),
            new NavItem("feedback", L("Nav.Feedback"), IsSecondary: true),
            new NavItem("music", L("Nav.Music"), IsSecondary: true),
            new NavItem("about", L("Nav.About"), IsSecondary: true)
        ]
    };

    /// <summary>
    /// 按「要隐藏哪些入口」过滤菜单项。
    ///
    /// 对应 PCL 的 <c>HiddenRefresh()</c>：把被藏起来的入口从左栏里去掉。
    /// </summary>
    private NavItem[] FilterHidden(NavItem[] items) =>
        items.Where(i => !HiddenToolKeys.Contains(i.Key)).ToArray();
}
