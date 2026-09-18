using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UMango.App.ViewModels;

namespace UMango.App;

/// <summary>
/// 主窗口外壳：顶部 5 个 banner 选项卡（主页/下载/进度/设置/工具）+ 左侧随选项卡变化的菜单
/// + 右侧 5 个页面（HomeView / DownloadView / ProgressView / SettingsView / ToolsView）。
///
/// 职责边界（这是本文件唯一需要记住的约定）：
///   · 外壳只把 <see cref="ShellViewModel"/> 的状态映射成「顶部勾选 / 左栏菜单与高亮 / 右区页面可见性」；
///   · 页面由各自的 XAML 创建（页面自己 new 自己的 ViewModel、自己在 Loaded 里异步加载），
///     外壳不 new、不缓存、不触碰它们的 ViewModel，也不替它们做数据加载；
///   · 选项卡内的二级导航（例如下载页的"搜索结果"）属于页面自己的事，
///     外壳只把选中项写进 <see cref="ShellViewModel.CurrentNavKey"/>，页面想用就绑它。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>外壳 ViewModel（选项卡 + 左栏菜单的唯一真相来源）。</summary>
    private readonly ShellViewModel _shell = new();

    /// <summary>
    /// 接线是否完成。
    /// 为什么必须有：XAML 里 TabHome 的 IsChecked="True" 会在 InitializeComponent **期间**
    /// 触发 Checked，而那一刻左栏 ListBox（在 XAML 里排在选项卡之后）还没被创建出来；
    /// 此时去同步左栏会直接 NullReferenceException。所以凡是"改界面"的响应都先看这个开关。
    /// </summary>
    private bool _wired;

    /// <summary>正在按 VM 反向设置顶部勾选（避免 Checked 回调再回写 VM 造成回环）。</summary>
    private bool _syncingTab;

    /// <summary>正在按 VM 反向设置左栏选中项（避免 SelectionChanged 回调再回写 VM 造成回环）。</summary>
    private bool _syncingNav;

    public MainWindow()
    {
        InitializeComponent();

        // 左栏 ItemsSource 用 Binding.Source 直接指 VM，而不是设 Window.DataContext：
        // 五个页面的 DataContext 是各自的 ViewModel，外壳若把 ShellViewModel 挂到窗口 DataContext 上，
        // 任何"忘了显式设 DataContext"的页面都会静默继承它，于是 {Binding Title} 之类会串到外壳的属性上。
        LeftMenuList.SetBinding(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(ShellViewModel.LeftMenu)) { Source = _shell });

        // 菜单被重建（换选项卡）→ 重新选第一项：ItemsSource 换成新集合后 WPF 会清空选中项，
        // 不补这一步就会出现"左栏没有高亮项、右区却已经切页"的错位。
        _shell.LeftMenuChanged += SyncLeftSelection;

        // 二级导航变化 → 由外壳裁决是否要换成独立页面（当前只有"下载器"这一例）。
        _shell.NavChanged += _ => ApplyNavOverride();

        // 选项卡也可能被别处改（快捷键、将来页面内的跳转）→ 跟着切右区、勾选顶部 banner。
        _shell.PropertyChanged += OnShellPropertyChanged;

        _wired = true;

        // 构造完成后按 VM 的初始状态把界面刷一遍（默认是「主页」）。
        SyncTabCheck();
        SyncLeftSelection();
        ShowTabContent(_shell.CurrentTab);
    }

    // ================= 顶部选项卡 =================

    /// <summary>顶部 banner 选项卡被勾选（Tag 保持字符串形式，与改造前一致）。</summary>
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // 两种要忽略的情况：
        //   · 构造期的首次勾选（_wired == false，界面还没就绪）；
        //   · 由 SyncTabCheck 反向同步勾选（_syncingTab == true，那是 VM 状态的投影，再回写就是回环）。
        if (!_wired || _syncingTab)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tag } && TryParseTab(tag, out var tab))
        {
            // 只改 VM：右区切换、左栏重建都由 VM 的通知驱动（见 OnShellPropertyChanged 与 SyncLeftSelection）。
            _shell.CurrentTab = tab;
        }
    }

    /// <summary>
    /// Tag 字符串 → <see cref="AppTab"/>。
    /// 为什么用 switch 而不是在 Tag 里直接放枚举：Tag 是 XAML 字面量，写字符串可读性最好，
    /// 且未知 Tag 时能明确地"忽略"而不是抛异常（设计器里手改 Tag 不会让窗口崩掉）。
    /// </summary>
    private static bool TryParseTab(string tag, out AppTab tab)
    {
        switch (tag)
        {
            case "home": tab = AppTab.Home; return true;
            case "download": tab = AppTab.Download; return true;
            case "progress": tab = AppTab.Progress; return true;
            case "settings": tab = AppTab.Settings; return true;
            case "tools": tab = AppTab.Tools; return true;
            default: tab = AppTab.Home; return false;
        }
    }

    /// <summary>把顶部 banner 的勾选状态对齐到 VM（VM 是唯一真相来源）。</summary>
    private void SyncTabCheck()
    {
        _syncingTab = true;
        try
        {
            // 同一个 StackPanel 里的 RadioButton 自动成组：把目标项置 true 时，
            // 前一个被勾选的项会自动变 false（只触发 Unchecked，不会触发 Checked），
            // 因此这段代码最多触发一次 Checked —— 且被 _syncingTab 拦住。
            TabHome.IsChecked = _shell.CurrentTab == AppTab.Home;
            TabDownload.IsChecked = _shell.CurrentTab == AppTab.Download;
            TabProgress.IsChecked = _shell.CurrentTab == AppTab.Progress;
            TabSettings.IsChecked = _shell.CurrentTab == AppTab.Settings;
            TabTools.IsChecked = _shell.CurrentTab == AppTab.Tools;
        }
        finally
        {
            _syncingTab = false;
        }
    }

    /// <summary>VM 的属性变化：目前只关心选项卡（切右区 + 勾选顶部 + 兜底同步左栏）。</summary>
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_wired || e.PropertyName != nameof(ShellViewModel.CurrentTab))
        {
            return;
        }

        SyncTabCheck();
        ShowTabContent(_shell.CurrentTab);

        // 兜底：换选项卡时左栏本来会由 LeftMenuChanged 刷新，这里再来一次是幂等的，
        // 目的是"无论通知的先后顺序如何，左栏都不会与当前选项卡脱节"。
        SyncLeftSelection();
    }

    /// <summary>切换右区内容：五个页面叠放，只改可见性（不重建，页面状态得以保留）。</summary>
    private void ShowTabContent(AppTab tab)
    {
        ViewHome.Visibility = VisibleIf(tab == AppTab.Home);
        ViewDownload.Visibility = VisibleIf(tab == AppTab.Download);
        ViewProgress.Visibility = VisibleIf(tab == AppTab.Progress);
        ViewSettings.Visibility = VisibleIf(tab == AppTab.Settings);
        ViewTools.Visibility = VisibleIf(tab == AppTab.Tools);

        // 二级导航可能仍停在"下载器"上，切完选项卡要重新裁决一次显示的是谁
        ApplyNavOverride();
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 选项卡内的二级导航覆盖。
    ///
    /// 外壳不替页面做二级导航（页面自己响应 CurrentNavKey），但**下载器是个例外**：
    /// 它是一个独立页面（不是 ToolsView 内部的一个分区），放在 PCL-CE 「百宝箱」的位置，
    /// 所以由外壳在这里把 ToolsView 换成 DownloaderView。
    /// 这样 ToolsView 无需知道下载器的存在，改动面最小。
    /// </summary>
    private void ApplyNavOverride()
    {
        var downloaderSelected = _shell.CurrentTab == AppTab.Tools
                                 && _shell.CurrentNavKey == "downloader";

        ViewDownloader.Visibility = VisibleIf(downloaderSelected);

        // 选中下载器时把 ToolsView 收起，否则两个页面会叠在一起
        if (downloaderSelected)
        {
            ViewTools.Visibility = Visibility.Collapsed;
        }
        else if (_shell.CurrentTab == AppTab.Tools)
        {
            ViewTools.Visibility = Visibility.Visible;
        }
    }

    // ================= 左栏菜单 =================

    /// <summary>左栏点击：把选中项交给 VM（唯一真相来源），高亮由 ListBoxItem 的选中态自己呈现。</summary>
    private void LeftMenuList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_wired || _syncingNav)
        {
            return;
        }

        // 只处理"选中了某一项"：换选项卡时 WPF 会先清空选中项（AddedItems 为空），
        // 那是中间态而不是用户操作，忽略即可。
        if (e.AddedItems.Count == 0)
        {
            return;
        }

        if (e.AddedItems[0] is NavItem nav)
        {
            _shell.SelectNavCommand.Execute(nav);
        }
    }

    /// <summary>
    /// 把左栏选中项对齐到 VM 的 <see cref="ShellViewModel.CurrentNavKey"/>。
    /// 菜单项本身（含两行渲染与高亮）由 XAML 的 NavItemTemplate / NavItemStyle 负责。
    /// </summary>
    private void SyncLeftSelection()
    {
        if (!_wired)
        {
            return;
        }

        // 先把绑定拉一次：Menu 变化 → 绑定的 ItemsSource 必须已经跟上，
        // 否则下面按 Key 找到的项会落在一个"上一版菜单"上，选中项永远设不进去。
        LeftMenuList.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();

        var menu = _shell.LeftMenu;

        // 按 Key 定位而不是按引用：换选项卡时菜单是重新构造的一组 NavItem 实例，引用比较必然选不中。
        var current = menu.FirstOrDefault(i => i.Key == _shell.CurrentNavKey) ?? menu.FirstOrDefault();

        _syncingNav = true;
        try
        {
            LeftMenuList.SelectedItem = current;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    // ================= 三大金刚键（Windows 原生行为） =================

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
