using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UMango.App.Services;
using UMango.App.Themes;
using UMango.App.ViewModels;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Core.Localization;
using UMango.App.Localization;

namespace UMango.App.Views;

/// <summary>
/// 记录列表的一行。
/// 为什么在视图侧做这层投影（而不是用 IValueConverter 直接绑定 Core 的 InstallRecord）：
///   · 时间格式化、枚举显示名（PackageSourceKind.DisplayName 是扩展方法，XAML 绑不到）、
///     操作名中文化、耗时可读化都需要转换器，而本次改动只允许新建这两个文件；
///   · 投影成字符串后 XAML 只做纯绑定，也不会出现"转换器返回空 → 界面静默显示空白"这种难查的失败。
/// </summary>
public sealed record RecordItem(
    string Time,
    string Source,
    string Package,
    string Version,
    bool HasVersion,
    string Action,
    bool Success,
    string ResultText,
    string Duration,
    string ErrorText,
    bool HasError);

/// <summary>某个包源用过的次数（已按次数降序）；BarWidth 是按最大次数等比缩放后的条形宽度。</summary>
public sealed record SourceCountItem(string Source, int Count, string CountText, double BarWidth);

/// <summary>
/// 设置页：三张卡片（主题与外观 / 调试 / 记录管理）。
///
/// 职责边界（为什么这样切）：
///   · 主题的**切换动作**只发生在 SettingsViewModel 里（它调 ThemeManager.Apply 并给出状态反馈），
///     页面只把"用户选了哪个主题族、深色开没开"告诉它 —— 两处都实现会互相打架；
///   · 记录**明细的格式化**在视图侧完成（Core 的 InstallRecord 面向存储，不面向界面）；
///   · 清空记录是破坏性操作，而二次确认属于纯视图关注点（MessageBox），故封装成本页自己的命令。
///
/// 主题即时生效的做法：本页（与 Styles.xaml）所有颜色/字体引用都是 DynamicResource，
/// ThemeManager 换掉 Application.Resources 里的 token 字典后，WPF 会重新解析这些引用，
/// 无需重启、也无需重建页面。
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>最近记录最多显示多少条（再多也不适合一屏翻看，页面本身可滚动）。</summary>
    private const int MaxRecentRecords = 50;

    /// <summary>按包源条形图的最长/最短宽度（px），保证次数悬殊时也看得见。</summary>
    private const double MaxBarWidth = 160;
    private const double MinimumBarWidth = 4;

    private readonly ObservableCollection<SourceCountItem> _sourceCounts = [];
    private readonly ObservableCollection<RecordItem> _recordItems = [];

    /// <summary>主题事件是否已挂上（Loaded/Unloaded 会成对触发，避免重复订阅）。</summary>
    private bool _themeHooked;

    /// <summary>当前 ViewModel（本页只创建它一次，全生命周期复用）。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// 「清空记录」命令：先二次确认，确认后才转发给 <see cref="SettingsViewModel.ClearRecordsCommand"/>。
    /// 暴露成属性是为了让 XAML 用 ElementName 绑定 —— 本页 DataContext 是 ViewModel，
    /// 而视图自己的命令不在 DataContext 上。
    /// </summary>
    public ICommand ClearRecordsCommand { get; }

    /// <summary>外壳用 new SettingsView() 直接创建页面，因此必须是参数less 构造函数。</summary>
    public SettingsView()
    {
        // 先建命令、再解析 XAML：XAML 里的 ElementName 绑定在解析阶段就可能去读 ClearRecordsCommand，
        // 若那时属性还是 null，绑定会一直停在空值上（普通 CLR 属性不会收到变更通知）。
        ClearRecordsCommand = new ConfirmRequiredCommand(ConfirmClearRecords, ExecuteClearRecords);

        InitializeComponent();

        // 页面自己 new 自己的 ViewModel（服务从 AppServices 取），外壳不需要注入任何东西。
        ViewModel = new SettingsViewModel();
        DataContext = ViewModel;

        // 记录明细与"按包源次数"是视图侧整理出来的集合，直接赋值而不是绑定 VM 属性：
        // SettingsViewModel（另一位 agent 的文件）没有暴露这些投影类型，也不该为此改动它。
        SourceCountList.ItemsSource = _sourceCounts;
        RecordList.ItemsSource = _recordItems;

        // 先用内存中已有的记录画一遍，Loaded 时再去磁盘核对（见 SettingsView_Loaded）。
        RefreshRecordViews();
    }

    /// <summary>
    /// 首次显示：挂主题事件 + 从磁盘重读记录。
    ///
    /// Core 的 <see cref="InstallRecordStore.Load"/> 是同步 API，直接调用会占用 UI 线程做文件 I/O
    /// （记录文件可能落在 OneDrive/网络盘上，慢 I/O 会让页面卡一下），所以丢到后台线程 await，
    /// await 之后自动回到 UI 线程再更新控件。绝不能改用 .Result/.Wait() —— UI 线程上同步等待会死锁。
    /// </summary>
    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        HookTheme();

        try
        {
            await Task.Run(() => AppServices.Records.Load());

            RefreshRecordViews();
            UpdateThemeCaption();
        }
        catch (Exception ex)
        {
            // 读盘失败/记录文件损坏都不该让设置页崩掉（内存里的记录仍然可用），
            // 但必须把原因说出来而不是假装成功。
            // 注意这类 async void 处理器里漏出去的异常会直接终止进程，所以整段都包在 try 里。
            ViewModel.StatusMessage = ViewText.L("Settings.Records.ReadFailed", ex.Message);
        }
    }

    /// <summary>页面卸载（外壳重建页面）时退订，避免 ThemeManager 长期持有已死页面的引用。</summary>
    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_themeHooked) return;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _themeHooked = false;
    }

    private void HookTheme()
    {
        if (_themeHooked) return;
        ThemeManager.ThemeChanged += OnThemeChanged;
        _themeHooked = true;
    }

    /// <summary>
    /// 页面从隐藏变为可见时补一次刷新。
    ///
    /// 为什么需要：外壳是用 Visibility 切页的（页面只创建一次，不重建），
    /// 因此"切回设置页"不会再触发 Loaded —— 不补这一下，用户装完东西再回到设置页，
    /// 看到的次数与记录会停在启动那一刻。
    /// 这里只读**内存中**的记录（不碰磁盘、不做 I/O），所以既不会卡顿，
    /// 也不会与「重新加载」按钮（重读磁盘）的语义重复。
    /// </summary>
    private void SettingsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        try
        {
            RefreshRecordViews();
        }
        catch (Exception ex)
        {
            // 这个方法同样在 UI 事件回调里，异常漏出去会终止进程，因此照例挡住。
            ViewModel.StatusMessage = ViewText.L("Settings.Records.RefreshFailed", ex.Message);
        }
    }

    // ==================== ①.7 语言 ====================

    /// <summary>
    /// 语言被选中：选中即应用。
    ///
    /// 与主题的差别：**不判断"是否真的变化"**就应用 —— 语言切换需要重建资源字典，
    /// 而 <see cref="Localization.AppLocalization.SetLanguage"/> 内部由
    /// <see cref="Core.Localization.LocalizationService.Apply"/> 负责判断变化与否
    /// （返回 false 时重建字典是无害的等幂操作，代价是几毫秒）。
    /// </summary>
    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is Localization.AppLocalization.LanguageOption option)
        {
            ViewModel.SelectedLanguage = option;
            ViewModel.ApplyLanguageCommand.Execute(null);
        }
    }

    // ==================== ① 主题与外观 ====================

    /// <summary>主题族被选中：选中即应用（SelectedItem 已 TwoWay 绑回 ViewModel）。</summary>
    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 不依赖"绑定先于事件"这种隐含顺序：直接以控件当前的选中项为准写回 ViewModel。
        if (ThemeList.SelectedItem is ThemeOption option)
        {
            ViewModel.SelectedTheme = option;
        }

        ApplyThemeIfChanged();
    }

    /// <summary>深色模式开关：切换后立即重应用主题（实时生效）。</summary>
    private void DarkToggle_Click(object sender, RoutedEventArgs e)
    {
        // 用 Click 而不是 Checked/Unchecked：Click 只在用户交互时触发，
        // 程序化改 IsChecked（例如主题被别处切换后 ViewModel 把状态同步过来）不会触发，
        // 因此不会出现"同步界面 → 又反过来应用主题"的回环。
        if (sender is CheckBox box)
        {
            ViewModel.IsDark = box.IsChecked == true;
        }

        ApplyThemeIfChanged();
    }

    /// <summary>跟随系统开关：勾上后深浅色交给操作系统决定。</summary>
    private void FollowSystemToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box)
        {
            ViewModel.FollowSystem = box.IsChecked == true;
        }

        ApplyThemeIfChanged();
    }

    /// <summary>
    /// 把用户当前的选择落到主题上。与 ThemeManager 的现状一致时什么都不做：
    /// ListBox 初始化选中、ViewModel 自身同步都会走到这里，若无差别地 Apply，
    /// 启动时会白换一次资源字典，并让状态栏显示"已应用主题"这种并不存在的用户操作。
    /// </summary>
    private void ApplyThemeIfChanged()
    {
        var option = ViewModel.SelectedTheme;
        if (option is null) return;
        if (ThemeManager.CurrentKind == option.Kind && ThemeManager.IsDark == ViewModel.IsDark) return;

        // 不直接调 ThemeManager.Apply：Apply 的唯一入口留在 ViewModel（它同时负责状态反馈，
        // 并在主题字典缺失时给出可读的失败原因）。页面只负责告诉它用户选了什么。
        ViewModel.ApplyThemeCommand.Execute(null);
        UpdateThemeCaption();
    }

    /// <summary>
    /// 灰度模式开关：只把值写回 ViewModel（进而落到设置项），
    /// 实际脱色由 App 上的 <c>GrayModeController</c> 完成。
    /// </summary>
    private void GrayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box) ViewModel.GrayMode = box.IsChecked == true;
    }

    /// <summary>ThemeManager 的事件（Apply 内部已编组到 UI 线程），刷新"当前生效字典"回显。</summary>
    private void OnThemeChanged() => UpdateThemeCaption();

    /// <summary>
    /// 回显当前**真正生效**的 token 字典。读的是 ThemeManager 的权威状态而不是界面的选择项，
    /// 因此"切换是否真的生效"一眼可见（这是主题即时生效的直接证据）。
    /// </summary>
    private void UpdateThemeCaption()
    {
        var family = ViewModel.SelectedTheme?.Title ?? ThemeManager.CurrentKind.ToString();
        CurrentThemeText.Text =
            ViewText.L("Settings.Records.Current", family,
                ViewText.L(ThemeManager.IsDark ? "Settings.Dark" : "Settings.Light"),
                ThemeManager.CurrentDictionaryPath);
    }

    // ==================== ③ 记录管理 ====================

    /// <summary>「重新加载」：从磁盘重读记录（另一个实例或外部工具可能改过记录文件）。</summary>
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 同 Loaded：同步的 Core API 放后台线程，await 回到 UI 线程后再改界面。
            await Task.Run(() => AppServices.Records.Load());

            // 统计、按包源次数、明细一律从 Core 存储重新取（见 RefreshRecordViews），
            // 因此"重新加载"不需要重建 ViewModel，也不会让主题选中状态/深色开关被重置。
            RefreshRecordViews();
            ViewModel.StatusMessage = ViewText.L("Settings.Records.Reloaded", AppServices.Records.TotalCount);
        }
        catch (Exception ex)
        {
            // 同 Loaded：async void 处理器里漏出的异常会终止进程，整段都要包住。
            ViewModel.StatusMessage = ViewText.L("Settings.Records.ReloadFailed", ex.Message);
        }
    }

    /// <summary>确认通过后真正执行清空（仍走 ViewModel 的命令，业务逻辑与状态消息由它负责）。</summary>
    private void ExecuteClearRecords()
    {
        ViewModel.ClearRecordsCommand.Execute(null);
        RefreshRecordViews();   // 明细来自 Core 存储，视图侧重建一次
    }

    /// <summary>
    /// 二次确认。清空记录是**不可撤销**的破坏性操作（Core 的 Clear 会立刻把空列表落盘，
    /// 界面上没有任何恢复途径），所以执行前必须让用户明确点一次"确定"。
    ///
    /// 为什么把确认封进命令、而不是写在按钮的 Click 里：Click 事件在 Command 执行**之前**触发，
    /// 且 Click 处理器没有任何办法取消随后的命令执行 —— 只有在命令的 Execute 内部先问再执行，
    /// 才能保证"没确认就绝不清空"。
    /// </summary>
    private bool ConfirmClearRecords()
    {
        var message = ViewText.L("Settings.Records.ClearConfirm");
        var caption = ViewText.L("Settings.Records.ClearCaption");

        // 有宿主窗口时带上 owner，保证对话框模态于主窗口并居中；无窗口宿主（设计器等）退回无 owner 版本。
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show(message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }

    /// <summary>
    /// 重建整块「记录管理」的内容：统计数字、按包源次数、最近记录。
    /// 数据源是 Core 的 <see cref="InstallRecordStore"/>（唯一权威），视图侧只做格式化、不缓存业务状态，
    /// 因此这里可以随时整块重取（首次显示、重新加载、清空记录之后各调一次即可）——
    /// 三条数据来自同一次读取，界面上不会出现"数字与明细对不上"。
    /// </summary>
    private void RefreshRecordViews()
    {
        var store = AppServices.Records;

        // ---- 统计（总次数/成功/失败）----
        StatTotalText.Text = store.TotalCount.ToString(CultureInfo.InvariantCulture);
        StatSuccessText.Text = store.SuccessCount.ToString(CultureInfo.InvariantCulture);
        StatFailureText.Text = store.FailureCount.ToString(CultureInfo.InvariantCulture);

        // ---- 按包源的次数：降序；没用过的包源不显示（"0 次"的行没有信息量）----
        _sourceCounts.Clear();
        var counts = store.CountBySource
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.DisplayName(), StringComparer.Ordinal)
            .ToList();

        var max = counts.Count == 0 ? 0 : counts[0].Value;
        foreach (var (kind, count) in counts)
        {
            // 条形宽度按最大次数等比缩放，最少 MinimumBarWidth：次数相差悬殊时也还能看见一条。
            var barWidth = max <= 0
                ? MinimumBarWidth
                : Math.Max(MinimumBarWidth, MaxBarWidth * count / max);
            _sourceCounts.Add(new SourceCountItem(kind.DisplayName(), count, ViewText.L("Settings.Records.Times", count), barWidth));
        }

        SourceCountEmpty.Visibility = _sourceCounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 最近记录：store.Records 本身就是"最近在前"，取 50 条够一屏翻看 ----
        _recordItems.Clear();
        foreach (var item in store.Records.Take(MaxRecentRecords))
        {
            var version = item.Version ?? string.Empty;
            var errorCode = item.ErrorCode ?? string.Empty;

            _recordItems.Add(new RecordItem(
                Time: item.OccurredAt.LocalDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Source: item.Source.DisplayName(),
                Package: item.PackageName,
                Version: version,
                HasVersion: version.Length > 0,
                Action: ActionDisplayName(item.Action),
                Success: item.Success,
                ResultText: ViewText.L(item.Success ? "Record.Result.Success" : "Record.Result.Failure"),
                Duration: FormatDuration(item.DurationMs),
                ErrorText: errorCode.Length == 0 ? string.Empty : ViewText.L("Record.Detail.ErrorCode", errorCode),
                HasError: errorCode.Length > 0));
        }

        RecordEmpty.Visibility = _recordItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 记录文件位置：排查"记录去哪了"最常需要的一句话信息。
        RecordStorePathText.Text = ViewText.L("Home.RecordFile", store.StorePath);
        RecordStorePathText.ToolTip = store.StorePath;
    }

    /// <summary>操作类型中文化。未知值原样显示：Core 以后新增操作类型时界面既不会空白也不会崩。</summary>
    private static string ActionDisplayName(string action) => action switch
    {
        "install" => ViewText.L("Record.Op.Install"),
        "uninstall" => ViewText.L("Record.Op.Uninstall"),
        "upgrade" => ViewText.L("Record.Op.Upgrade"),
        _ => string.IsNullOrWhiteSpace(action) ? "—" : action,
    };

    /// <summary>
    /// 耗时可读化：不足 1 秒用毫秒，超过 1 分钟用「分 秒」，其余用秒（两位小数）。
    /// 0（Core 表示未记录到耗时）显示成破折号，避免被误读成"瞬间完成"。
    /// </summary>
    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0) return "—";
        if (milliseconds < 1000) return $"{milliseconds} ms";

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalMinutes >= 1
            ? ViewText.L("Settings.Records.Duration", (int)span.TotalMinutes, span.Seconds)
            : ViewText.L("Settings.Records.DurationSeconds", span.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 「先二次确认，再执行」的命令包装。
    /// 之所以需要它：按钮的 Click 事件发生在其 Command 执行之前，且 Click 处理器无法阻止随后的
    /// 命令执行 —— 只有把确认放进 Execute 内部，"没确认就不执行"才是真正成立的。
    /// </summary>
    private sealed class ConfirmRequiredCommand : ICommand
    {
        private readonly Func<bool> _confirm;
        private readonly Action _execute;

        public ConfirmRequiredCommand(Func<bool> confirm, Action execute)
        {
            _confirm = confirm;
            _execute = execute;
        }

        /// <summary>该命令恒可执行，因此不需要任何变更通知（空实现即可）。</summary>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (!_confirm()) return;   // 用户取消：什么都不做，记录一条不少
            _execute();
        }
    }

    /// <summary>代理模式切换：应用代理设置，并显示/隐藏自定义代理面板。</summary>
    private void ProxyModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem is SettingsViewModel.ProxyModeOption option)
            ViewModel.SelectedProxyMode = option;
        UpdateProxyCustomVisibility();
    }

    /// <summary>代理密码改动：PasswordBox 没有可绑定的 Password 属性，只能走事件。</summary>
    private void ProxyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            ViewModel.ProxyPassword = box.Password;
    }

    /// <summary>自定义代理面板只在「自定义」模式下可见。</summary>
    private void UpdateProxyCustomVisibility()
    {
        if (ProxyCustomPanel is null)
            return;
        var isCustom = ViewModel.SelectedProxyMode?.Mode == Core.Network.ProxyMode.CustomProxy;
        ProxyCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

}
