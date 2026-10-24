using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace UMango.App.ViewModels;

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
/// 规则（对应 PCL-CE 的次级页面机制）：
///   每个选项卡的**第一项是主视图**，其余项都是次级页面。
///   进入次级页面会压栈（顶部出现「← 返回 + 页面标题」），
///   点返回会回到上一个视图，并把左栏高亮一并还原。
/// </summary>
public sealed record NavItem(string Key, string Title, string? Subtitle = null, bool IsSecondary = false);

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
        var items = MenuFor(tab);
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
    private static NavItem[] MenuFor(AppTab tab) => tab switch
    {
        AppTab.Home =>
        [
            new NavItem("overview", L("Nav.Overview")),
            new NavItem("records", L("Nav.Records"), IsSecondary: true)
        ],
        AppTab.Download =>
        [
            new NavItem("sources", L("Nav.Sources")),
            new NavItem("results", L("Nav.Results"), IsSecondary: true),
            new NavItem("favorites", L("Nav.Favorites"), IsSecondary: true)
        ],
        AppTab.Progress =>
        [
            new NavItem("running", L("Nav.Running")),
            new NavItem("finished", L("Nav.Finished"), IsSecondary: true)
        ],
        AppTab.Settings =>
        [
            new NavItem("theme", L("Nav.Theme")),
            new NavItem("debug", L("Nav.Debug"), IsSecondary: true),
            new NavItem("recordManage", L("Nav.RecordManage"), IsSecondary: true)
        ],
        _ =>
        [
            // 下载器放在工具页下，对应 PCL-CE 的「百宝箱」位置
            new NavItem("downloader", L("Nav.Downloader")),
            new NavItem("logs", L("Nav.Logs"), IsSecondary: true),
            new NavItem("diagnostics", L("Nav.Diagnostics"), IsSecondary: true),
            new NavItem("licenses", L("Nav.Licenses"), IsSecondary: true),
            new NavItem("feedback", L("Nav.Feedback"), IsSecondary: true),
            new NavItem("music", L("Nav.Music"), IsSecondary: true),
            new NavItem("about", L("Nav.About"), IsSecondary: true)
        ]
    };
}
