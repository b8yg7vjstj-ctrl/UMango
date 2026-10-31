// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 壳的主窗口代码 —— 与 WPF 壳（`src/UMango.App/MainWindow.xaml.cs`）**同构**。
//
// 职责边界（两个壳完全一致）：
//   · 外壳只把 `ShellViewModel` 的状态映射成「顶部 banner 高亮 / 左栏菜单与高亮 / 右区页面可见性」；
//   · 页面由各自的 axaml 创建（页面自己 new 自己的 ViewModel、自己在 Loaded 里异步加载），
//     外壳不 new、不缓存、不触碰它们的 ViewModel；
//   · 选项卡内的二级导航（例如下载页的"搜索结果"）属于页面自己的事，
//     外壳只把选中项写进 `ShellViewModel.CurrentNavKey`。
//
// 唯一的差异在**动效**：WPF 侧 PCL 主题走的是逐字采用的 `PCL.ModAnimation`（WPF 专属），
// Avalonia 侧走 Core 的补间层（`PclTweenPresets`），数值与 PCL 完全相同 ——
// 这样两个壳的观感一致，而代码不重复。

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UMango.Core.Animation;
using UMango.Core.UI.Navigation;
using UMango.Shell.Services;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia;

/// <summary>
/// 主窗口外壳：顶部 5 个 banner 选项卡 + 左侧随选项卡变化的菜单 + 右侧页面区。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>外壳 ViewModel（选项卡 + 左栏菜单的唯一真相来源）。</summary>
    private readonly ShellViewModel _shell = new();

    /// <summary>
    /// 接线是否完成。
    /// 为什么必须有：构造期间 XAML 还没建好左栏 ListBox，此时去同步左栏会崩。
    /// </summary>
    private bool _wired;

    /// <summary>正在按 VM 反向设置左栏选中项（避免回调再回写 VM 造成回环）。</summary>
    private bool _syncingNav;

    /// <summary>正在把导航栈的变化写回左栏（与 <see cref="_syncingNav"/> 是一对镜像开关）。</summary>
    private bool _syncingNavStack;

    /// <summary>
    /// 导航宿主：把 Core 的导航栈接到窗口上（副标题栏 + 返回按钮）。
    /// 与 WPF 侧同一个类型（`UMango.Shell.Services.NavigationHost`）—— 已在抽取时移入 Shell。
    /// </summary>
    private readonly NavigationHost _nav = new(
        new NavigationService(
            NavStackMapping.PageId(AppTab.Home, "overview"), "概览"));

    public MainWindow()
    {
        InitializeComponent();

        // 左栏 ItemsSource 绑到 VM（不设窗口 DataContext —— 页面各有自己的 DataContext，
        // 挂到窗口上会让"忘了显式设 DataContext"的页面静默继承外壳的绑定）。
        LeftMenuList.ItemsSource = _shell.LeftMenu;

        // 菜单被重建（换选项卡）→ 重新选第一项。
        _shell.LeftMenuChanged += SyncLeftSelection;

        // 二级导航变化 → 由外壳裁决是否要换成独立页面。
        _shell.NavChanged += OnNavChanged;
        _nav.Navigation.Navigated += OnNavigationStackChanged;

        // 选项卡也可能被别处改 → 跟着切右区、勾选顶部 banner。
        _shell.PropertyChanged += OnShellPropertyChanged;

        // 导航副标题栏：跟着导航宿主的状态刷新。
        _nav.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(NavigationHost.Subtitle):
                    SubTitleText.Text = _nav.Subtitle ?? string.Empty;
                    break;
                case nameof(NavigationHost.IsSecondary):
                    SubTitleBar.IsVisible = _nav.IsSecondary;
                    break;
                case nameof(NavigationHost.CanGoBack):
                    BtnBack.IsEnabled = _nav.CanGoBack;
                    break;
            }
        };

        _wired = true;

        // 构造完成后按 VM 的初始状态把界面刷一遍（默认是「主页」）。
        SyncTabCheck();
        SyncLeftSelection();
        SwitchNow(_shell.CurrentTab);
        SubTitleBar.IsVisible = false;
        BtnBack.IsEnabled = false;
    }

    // ================= 顶部 banner 选项卡 =================

    /// <summary>Tag 字符串 → <see cref="AppTab"/>（与 WPF 侧同一套规则）。</summary>
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

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!TryParseTab(tag, out var tab)) return;

        // 已经在这一页就别重复切（否则动画会重播）。
        if (_shell.CurrentTab == tab) return;

        _shell.CurrentTab = tab;
    }

    /// <summary>
    /// 把顶部 banner 的高亮状态对齐到 VM（VM 是唯一真相来源）。
    ///
    /// Avalonia 侧不需要 WPF 那个 <c>_syncingTab</c> 防回环开关：
    /// banner 是普通 <c>Button</c>（靠 "active" 样式类表示选中），
    /// 不存在 RadioButton 的 Checked 回调回写 VM 这条路径。
    /// </summary>
    private void SyncTabCheck()
    {
        SetTabActive(TabHome, _shell.CurrentTab == AppTab.Home);
        SetTabActive(TabDownload, _shell.CurrentTab == AppTab.Download);
        SetTabActive(TabProgress, _shell.CurrentTab == AppTab.Progress);
        SetTabActive(TabSettings, _shell.CurrentTab == AppTab.Settings);
        SetTabActive(TabTools, _shell.CurrentTab == AppTab.Tools);
    }

    /// <summary>
    /// 设置 banner 的选中样式。
    /// Avalonia 的 Button 没有 WPF 那种 RadioButton 自动成组，所以要**手动**加/去
    /// "active" 类（这也是 WPF 侧那样做的 —— 那边靠 RadioButton 自动成组）。
    /// </summary>
    private static void SetTabActive(Button button, bool active)
    {
        if (active) button.Classes.Add("active");
        else button.Classes.Remove("active");
    }

    /// <summary>VM 的属性变化：目前只关心选项卡。</summary>
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_wired || e.PropertyName != nameof(ShellViewModel.CurrentTab)) return;

        SyncTabCheck();
        SwitchNow(_shell.CurrentTab);

        // 兜底：无论通知顺序如何，左栏都不会与当前选项卡脱节。
        SyncLeftSelection();
    }

    // ================= 右区页面 =================

    /// <summary>切换右区内容：页面叠放，只改可见性（不重建，页面状态得以保留）。</summary>
    private void SwitchNow(AppTab tab)
    {
        ViewHome.IsVisible = tab == AppTab.Home;
        ViewDownload.IsVisible = tab == AppTab.Download;
        ViewProgress.IsVisible = tab == AppTab.Progress;
        ViewSettings.IsVisible = tab == AppTab.Settings;
        ViewTools.IsVisible = tab == AppTab.Tools;

        // 二级导航可能仍停在"下载器"上，切完选项卡要重新裁决一次显示的是谁。
        ApplyNavOverride();

        PlayContentAnimation();
    }

    /// <summary>
    /// 选项卡内的二级导航覆盖。
    ///
    /// 与 WPF 侧同一套规则：下载器 / 日志 / 反馈 / 音乐是**独立页面**
    /// （不是 ToolsView 内部的一个分区），放在 PCL-CE「百宝箱」的位置，
    /// 由外壳在这里把 ToolsView 换成对应页面 —— 这样 ToolsView 无需知道它们的存在。
    /// </summary>
    private void ApplyNavOverride()
    {
        var onTools = _shell.CurrentTab == AppTab.Tools;
        var key = _shell.CurrentNavKey;

        var downloaderSelected = onTools && key == "downloader";
        var logSelected = onTools && key == "logs";
        var feedbackSelected = onTools && key == "feedback";
        var musicSelected = onTools && key == "music";

        ViewDownloader.IsVisible = downloaderSelected;
        ViewLog.IsVisible = logSelected;
        ViewFeedback.IsVisible = feedbackSelected;
        ViewMusic.IsVisible = musicSelected;

        // 选中了这些独立页面之一时把 ToolsView 收起，否则两层内容会叠在一起。
        ViewTools.IsVisible = onTools && !downloaderSelected && !logSelected && !feedbackSelected && !musicSelected;
    }

    /// <summary>当前可见的页面（用于播动画）。</summary>
    private Control? CurrentVisibleView()
    {
        foreach (var candidate in new Control?[]
                 { ViewHome, ViewDownload, ViewProgress, ViewSettings, ViewTools, ViewDownloader, ViewLog, ViewFeedback, ViewMusic })
        {
            if (candidate is not null && candidate.IsVisible) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 给当前可见的页面播一次入场动画。
    ///
    /// 数值逐条对照 PCL-CE `MyPageRight.TriggerEnterAnimation`（MyPageRight.cs:552-600）：
    ///   · 起始态 Opacity=0、Y=-16；
    ///   · 淡入 100ms（FluentOut Weak = 1-(1-t)^2）；
    ///   · 两段落体 +5 / +11（250ms FluentOut / 350ms OutBack）**并行叠加**；
    ///   · 终态 Y = -16 + 5 + 11 = 0。
    ///
    /// 与 WPF 壳共用同一份 Core 预设（`PclTweenPresets.PageEnter`）→ 两个壳观感一致。
    /// </summary>
    private void PlayContentAnimation()
    {
        var visible = CurrentVisibleView();
        if (visible is null) return;

        // 动画关掉时不播（Core 的动画设置在 Shell 层，两壳共用）。
        if (!AppSettings.Animation.Enabled)
        {
            visible.Opacity = 1d;
            return;
        }

        var binder = AvaloniaTweenBinder.ForElement(visible);
        var opacity = binder.Resolve(TweenChannel.Opacity)!;
        var translateY = binder.Resolve(TweenChannel.TranslateY)!;

        // 起始态：透明 + 上方 16px（PCL 原版的入场起点）
        opacity.SetValue(0d);
        translateY.SetValue(PclTweenPresets.EnterFromY);

        AvaloniaTweenPlayer.Play(PclTweenPresets.PageEnter(), binder, fps: 60);
    }

    // ================= 左栏菜单 =================

    /// <summary>
    /// 左栏选中项变化 → 同步到导航栈。
    ///
    /// 规则（对应 PCL-CE 的次级页面机制）：
    ///   · 选到**次级**项 → 压栈（顶部出现「← 返回 + 标题」）；
    ///   · 选到**主**项 → 清栈回到根（不再显示返回按钮）。
    /// </summary>
    private void OnLeftSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_wired || _syncingNav) return;

        if (LeftMenuList.SelectedItem is not NavItem nav) return;

        _shell.CurrentNavKey = nav.Key;
    }

    /// <summary>
    /// 二级导航变化 → 同步到导航栈。
    /// 规则本身在 <see cref="NavStackMapping"/>（纯逻辑，可测）；这里只负责执行。
    /// </summary>
    private void OnNavChanged(string key)
    {
        ApplyNavOverride();

        if (_syncingNavStack) return;

        var item = _shell.LeftMenu.FirstOrDefault(i => i.Key == key);

        var entry = NavStackMapping.Resolve(item, _shell.CurrentTab, _nav.Navigation.Current.Page.Value);
        if (entry is null) return;

        if (entry.IsSecondary)
            _nav.Navigation.Push(entry);
        else
            _nav.Navigation.NavigateRoot(entry);
    }

    /// <summary>
    /// 导航栈变化 → 同步回左栏（选项卡 + 高亮项）。
    /// 栈里记着 `tab/key`，所以这里把两者一起还原。
    /// </summary>
    private void OnNavigationStackChanged(object? sender, NavigationSnapshot snapshot)
    {
        if (_syncingNavStack) return;

        if (snapshot.Current.Parameter is not string navKey) return;

        if (!NavStackMapping.TryParse(snapshot.Current.Page.Value, out var tab, out var parsedKey))
            return;

        _syncingNavStack = true;
        try
        {
            if (_shell.CurrentTab != tab)
                _shell.CurrentTab = tab;

            _shell.CurrentNavKey = parsedKey;
            SyncLeftSelection();
        }
        finally
        {
            _syncingNavStack = false;
        }
    }

    /// <summary>把左栏高亮对齐到 VM 的当前项（换选项卡后左栏会被重建，高亮会丢）。</summary>
    private void SyncLeftSelection()
    {
        var menu = _shell.LeftMenu;
        if (menu.Count == 0)
        {
            LeftMenuList.SelectedItem = null;
            return;
        }

        var current = menu.FirstOrDefault(i => i.Key == _shell.CurrentNavKey) ?? menu[0];

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

    // ================= 副标题栏 =================

    /// <summary>副标题栏的返回按钮：退回上一层（对应 PCL 的 <c>PageBack</c>）。</summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (!_nav.CanGoBack) return;

        _nav.Navigation.Back();
    }
}
