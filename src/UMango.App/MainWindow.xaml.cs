using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UMango.App.Animation;
using UMango.App.ViewModels;
using UMango.Core.Animation;

using UMango.App.Themes;

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

    /// <summary>
    /// 任务栏进度桥接（Windows 专用）。放在字段里是为了在 Closed 时释放，
    /// 否则 watcher 会一直订阅 TaskCenter，窗口关掉后仍在无谓重算。
    /// </summary>
    private Services.TaskbarProgressBridge? _taskbar;

    /// <summary>系统通知桥接（Windows 专用，其它平台上是空转）。</summary>
    private Services.ToastBridge? _toast;

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

        // 全局提示浮层：PCL 的 HintWrapper 经 PclUiBridge 汇入 NotificationService，
        // 再汇入 Core 的提示队列（HintService）。
        // 渲染**不**直接挂 OnShow —— 按 PCL 的做法，由下面的心跳 Tick 驱动队列出队，
        // 这样"去重 / 条数上限 / 停留时长"只有一处规则（Core 的 HintService）。
        Services.AppServices.Notifications.Hints.Cleared += ClearToasts;
        _hintPump = new Services.HintPump(Services.AppServices.Notifications.Hints);
        _hintTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _hintTimer.Tick += (_, _) => PumpHints();
        _hintTimer.Start();

        // 选项卡也可能被别处改（快捷键、将来页面内的跳转）→ 跟着切右区、勾选顶部 banner。
        _shell.PropertyChanged += OnShellPropertyChanged;

        // 自定义主页卡片里的快捷动作 → 跳到对应页面（声明式主页的落点）。
        Services.AppServices.Homepage.NavigationRequested += NavigateFromHomepage;

        // 公告按钮里的快捷动作 → 同样跳到对应页面（白名单已限制在五个页面内）。
        Services.AppServices.Announcements.NavigationRequested += NavigateFromHomepage;

        _wired = true;

        // 构造完成后按 VM 的初始状态把界面刷一遍（默认是「主页」）。
        SyncTabCheck();
        SyncLeftSelection();
        ShowTabContent(_shell.CurrentTab);

        // 任务栏进度：把 TaskCenter 的聚合进度同步到任务栏图标。
        // 放在构造末尾：此时 TaskbarItemInfo 已由 InitializeComponent 建好。
        _taskbar = new Services.TaskbarProgressBridge(this);
        _taskbar.Start();

        // 系统通知：任务做完/失败时发一条 Windows Toast（窗口不在前台也能看到）。
        // 放在任务栏桥接旁边：两者都订阅 TaskCenter.Changed，同一时机启动、同一时机释放。
        _toast = new Services.ToastBridge();
        Closed += (_, _) => _ = DisposeTaskbarAsync();
    }

    /// <summary>
    /// 释放任务栏进度桥接。异步方法在事件处理器里不 await（fire-and-forget），
    /// 这里只需保证订阅被摘掉；真正的 DisposeAsync 内部同步完成退订。
    /// </summary>
    private async System.Threading.Tasks.Task DisposeTaskbarAsync()
    {
        if (_taskbar is not null)
        {
            await _taskbar.DisposeAsync();
            _taskbar = null;
        }

        if (_toast is not null)
        {
            _toast.Dispose();
            _toast = null;
        }
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
        // PCL 主题 + 动画开启时：先播 PCL 原版退场（70ms 上收），到位后再切可见性并播入场。
        // 退场调用形状逐字对照 PCL-CE MyPageRight.cs:618-623。
        if (ThemeManager.CurrentKind == ThemeKind.PclClassic && Services.AppSettings.Animation.Enabled)
        {
            var leaving = CurrentVisibleView();
            if (leaving is not null)
            {
                var aniList = new List<PCL.ModAnimation.AniData>
                {
                    PCL.ModAnimation.AaOpacity(leaving, -1, 70, 0),
                    PCL.ModAnimation.AaTranslateY(leaving, -6, 70, 0),
                };
                PCL.ModAnimation.AniStart(aniList, "UMango PageLeave");
                // 退场 70ms 后真正切换（与动画同步，期间旧页仍可见）
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(70)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    SwitchNow(tab);
                    PlayContentAnimation();
                };
                timer.Start();
                return;
            }
        }

        SwitchNow(tab);
        PlayContentAnimation();
    }

    /// <summary>
    /// 自定义主页动作触发的跳转。
    ///
    /// 目标名是**封闭集合**（Core 侧的动作白名单只认这五个），
    /// 所以这里不需要再做输入校验 —— 但仍然用 switch 显式列举，
    /// 未知目标直接忽略：白名单万一被扩展而这里忘了改，宁可不动也不要跳到未知页面。
    /// </summary>
    private void NavigateFromHomepage(string target)
    {
        switch (target)
        {
            case "downloads":
                _shell.SwitchTabCommand.Execute(AppTab.Download);
                break;
            case "settings":
                _shell.SwitchTabCommand.Execute(AppTab.Settings);
                break;
            case "tools":
                _shell.SwitchTabCommand.Execute(AppTab.Tools);
                break;
            case "logs":
                _shell.SwitchTabCommand.Execute(AppTab.Tools);
                // 工具页的左栏里「日志」是二级项：切过去还得把左栏选中项改成它，
                // 否则右区会显示工具页默认内容而不是日志页。
                _shell.SelectNavCommand.Execute("logs");
                break;
        }
    }

    /// <summary>立即切换可见性（不含动画时序）。</summary>
    private void SwitchNow(AppTab tab)
    {
        ViewHome.Visibility = VisibleIf(tab == AppTab.Home);
        ViewDownload.Visibility = VisibleIf(tab == AppTab.Download);
        ViewProgress.Visibility = VisibleIf(tab == AppTab.Progress);
        ViewSettings.Visibility = VisibleIf(tab == AppTab.Settings);
        ViewTools.Visibility = VisibleIf(tab == AppTab.Tools);

        // 二级导航可能仍停在"下载器"上，切完选项卡要重新裁决一次显示的是谁
        ApplyNavOverride();

        // 页面切换动画：方向矢量由全局设置决定（UMango 垂直 / PCL 兼容水平）。
        // 放在最后播，避免动画目标元素随后又被 ApplyNavOverride 隐藏掉。
        PlayContentAnimation();
    }

    /// <summary>
    /// 给当前可见的页面播一次入场动画。
    /// 位移方向、时长、缓动全部来自 Core 的 AnimationPlanner ——
    /// 这里不写任何"往哪个方向动"的判断，否则全局切换方向时会有页面漏改。
    /// </summary>
    private void PlayContentAnimation()
    {
        var visible = CurrentVisibleView();
        if (visible is null) return;

        // PCL 主题 + 动画开启：走 **PCL-CE 原版动画 API**（ModAnimation 是逐字采用的
        // PCL 代码本体，见 Pcl/ModAnimation.cs 文件头）。调用形状逐字对照
        // PCL-CE MyPageRight.cs:578-587 —— 这就是"动效在 PCL 主题中完整保留"的字面实现。
        if (ThemeManager.CurrentKind == ThemeKind.PclClassic && Services.AppSettings.Animation.Enabled)
        {
            visible.Opacity = 0d;
            visible.RenderTransform = new System.Windows.Media.TranslateTransform(0d, -16);
            var enterList = new List<PCL.ModAnimation.AniData>
            {
                PCL.ModAnimation.AaOpacity(visible, 1d, 100, 0,
                    new PCL.ModAnimation.AniEaseOutFluent(PCL.ModAnimation.AniEasePower.Weak)),
                PCL.ModAnimation.AaTranslateY(visible, 5d, 250, 0, new PCL.ModAnimation.AniEaseOutFluent()),
                PCL.ModAnimation.AaTranslateY(visible, 11d, 350, 0, new PCL.ModAnimation.AniEaseOutBack()),
            };
            PCL.ModAnimation.AniStart(enterList, "UMango PageEnter");
            return;
        }

        // Fluent10 主题（或用户改了设置）：走 Core 帧驱动引擎（数值来自 AnimationProfile）。
        // 关掉动画时 AnimationPlanner 会给出零时长规格，PlayEntrance 会直接复位到终态，
        // 因此这里不需要额外判断 AppSettings.Animation.Enabled。
        AnimationPlayer.PlayWithEngineFireAndForget(visible, ElementMotion.PageContent);
    }

    private FrameworkElement? CurrentVisibleView()
    {
        foreach (var candidate in new FrameworkElement?[]
                 { ViewHome, ViewDownload, ViewProgress, ViewSettings, ViewTools, ViewDownloader, ViewLog })
        {
            if (candidate?.Visibility == Visibility.Visible) return candidate;
        }
        return null;
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
        var onTools = _shell.CurrentTab == AppTab.Tools;
        var downloaderSelected = onTools && _shell.CurrentNavKey == "downloader";
        var logSelected = onTools && _shell.CurrentNavKey == "logs";

        ViewDownloader.Visibility = VisibleIf(downloaderSelected);
        ViewLog.Visibility = VisibleIf(logSelected);

        // 选中了这两个独立页面之一时把 ToolsView 收起，否则两层内容会叠在一起
        ViewTools.Visibility = VisibleIf(onTools && !downloaderSelected && !logSelected);
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

    // ================= 全局提示浮层 =================

    /// <summary>当前显示中的提示条（ObservableCollection 以便自动消失时移除）。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<ToastItem> _toasts = [];

    /// <summary>提示队列的心跳计时器（对应 PCL 的每帧 Tick）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _hintTimer;

    /// <summary>提示泵：把 Core 的队列动作翻译成「显示 / 撤掉」事件。</summary>
    private readonly Services.HintPump _hintPump;

    /// <summary>
    /// 心跳：向 Core 的提示队列要一步动作，并把结果画成右下角浮条。
    ///
    /// 这对应 PCL-CE 的 <c>HintService.Tick()</c> —— 队列里的提示**不是**来一条就画一条，
    /// 而是每个心跳拍出一批；去重与条数上限在 Core 里已经判过了，
    /// 这里只负责"把这一条画出来 / 把最旧的那条撤掉"。
    ///
    /// 实际的翻译逻辑在 <see cref="Services.HintPump"/>（无界面依赖，可单测）。
    /// </summary>
    private void PumpHints()
    {
        foreach (var ev in _hintPump.Pump())
        {
            switch (ev.Kind)
            {
                case Services.HintPumpEventKind.Show:
                    AddToast(ev.Text, ev.Type, ev.DurationMs);
                    break;

                case Services.HintPumpEventKind.Dismiss:
                    RemoveToast(ev.Text);
                    break;
            }
        }
    }

    /// <summary>画一条提示浮条，到时自动撤掉。</summary>
    private void AddToast(string message, UMango.Core.Notifications.HintType type, int durationMs)
    {
        var item = new ToastItem
        {
            Message = message,
            AccentBrush = AccentFor(type),
        };
        _toasts.Add(item);

        // 首条提示时才把集合挂上去（省掉 MainWindow 起步阶段的一次绑定开销）
        if (!ReferenceEquals(ToastHost.ItemsSource, _toasts)) ToastHost.ItemsSource = _toasts;

        // 到时移除。用 DispatcherTimer 而不是 Task.Delay：后者回调在后台线程，
        // 直接改 _toasts（绑定目标）会抛"集合被非拥有线程修改"。
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(800, durationMs)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 这条可能已经被"挤掉"提前撤走了 → 那时它早就不在集合里了，
            // 此时**不能**再去 NotifyDismissed：同文案可能已经重新入列并显示着，
            // 误报会把"已显示"列表里的那条一并抹掉（后面同文案的提示就能重复上屏了）。
            if (!_toasts.Contains(item)) return;
            _toasts.Remove(item);
            // 通知 Core：该条已经不在屏幕上了 → 从"已显示"列表移除，
            // 否则同样文案再来一次会被查重挡掉（而用户已经看不到它了）。
            Services.AppServices.Notifications.Hints.NotifyDismissed(message);
        };
        timer.Start();
    }

    /// <summary>撤掉指定文案的提示条（被更旧的挤掉时）。</summary>
    private void RemoveToast(string message)
    {
        var item = _toasts.FirstOrDefault(t => t.Message == message);
        if (item is null) return;
        _toasts.Remove(item);
        // 回报 Core 由 <see cref="Services.HintPump"/> 负责（它知道这是"淘汰"而不是"到期"）。
        // 这里不再重复回报，避免把同文案的其它条目也抹掉。
    }

    /// <summary>全部提示被清空时，把屏幕上的浮条一并撤掉。</summary>
    private void ClearToasts()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ClearToasts);
            return;
        }

        _toasts.Clear();
    }

    /// <summary>按提示类型取左侧色条颜色（走主题 token，明暗主题都会跟着变）。</summary>
    private System.Windows.Media.Brush AccentFor(UMango.Core.Notifications.HintType type) => type switch
    {
        UMango.Core.Notifications.HintType.Error => (System.Windows.Media.Brush)FindResource("BrError"),
        UMango.Core.Notifications.HintType.Warning => (System.Windows.Media.Brush)FindResource("BrWarnAmber"),
        UMango.Core.Notifications.HintType.Success => (System.Windows.Media.Brush)FindResource("BrProgressSuccess"),
        _ => (System.Windows.Media.Brush)FindResource("BrC3"),
    };

    /// <summary>提示条的数据项（只读：浮层里不需要双向绑定）。</summary>
    public sealed class ToastItem
    {
        public required string Message { get; init; }
        public required System.Windows.Media.Brush AccentBrush { get; init; }
    }
}
