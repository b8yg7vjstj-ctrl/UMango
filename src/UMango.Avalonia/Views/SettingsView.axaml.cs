// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧设置页 —— 与 WPF 版（`src/UMango.App/Views/SettingsView.xaml.cs`，475 行）同构。
//
// 页面职责边界（与 WPF 版一致）：
//   · 设置项本身（主题/语言/代理/背景/限额/调试/清空记录）全在 `SettingsViewModel` 里；
//   · 本页只做 XAML 表达不了的三件事：
//       1. 主题/语言的「选中即应用」（Avalonia 无触发器，必须显式调命令）；
//       2. 「记录管理」整块的现取现填与格式化（数据源是 Core 的 InstallRecordStore）；
//       3. 清空记录前的二次确认（不可撤销，必须挡在命令执行之前）。
//
// 与 WPF 的差异（都是框架差异，不是漏移植）：
//   · `MessageBox` → 自建的模态小窗口（Avalonia 没有内置 MessageBox）；
//   · `ThemeManager.CurrentDictionaryPath` → `AvaloniaThemeManager.ResolveRelativePath`；
//   · `IsVisibleChanged` → `PropertyChanged` 过滤 `IsVisibleProperty`（Avalonia 无该事件）；
//   · `PasswordBox.Password` → `TextBox.Text`（XAML 侧用 PasswordChar 掩码）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UMango.Avalonia.Themes;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Shell.Localization;
using UMango.Shell.Services;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>「最近记录」列表的一行（视图侧格式化后的展示模型）。</summary>
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

/// <summary>「按包源次数」列表的一行：条形宽度已按最大次数等比缩放。</summary>
public sealed record SourceCountItem(string Source, int Count, string CountText, double BarWidth);

/// <summary>设置页：主题与外观、功能隐藏、高级材质、动画、语言、更新、代理、公告、背景、窗口、限额、调试、记录管理。</summary>
public partial class SettingsView : UserControl
{
    /// <summary>条形最小宽度：次数相差悬殊时也还能看见一条。</summary>
    private const double MinimumBarWidth = 6;

    /// <summary>条形最大宽度（与 WPF 版同一标尺，保证两壳观感一致）。</summary>
    private const double MaxBarWidth = 240;

    /// <summary>最近记录最多显示条数：够一屏翻看即可，全量在 JSON 里。</summary>
    private const int MaxRecentRecords = 50;

    private readonly ObservableCollection<SourceCountItem> _sourceCounts = [];
    private readonly ObservableCollection<RecordItem> _recordItems = [];

    /// <summary>主题是否已挂钩 <see cref="AvaloniaThemeManager.ThemeChanged"/>（避免重复订阅）。</summary>
    private bool _themeHooked;

    /// <summary>正在把界面状态同步到主题管理器时置位，防止「同步 → 选中变更 → 再应用」的递归。</summary>
    private bool _applyingTheme;

    /// <summary>本页自己的 ViewModel（外壳统一用 <c>new SettingsView()</c> 创建，故构造必须无参）。</summary>
    public SettingsViewModel ViewModel { get; }

    public SettingsView()
    {
        InitializeComponent();

        ViewModel = new SettingsViewModel(ShellCapabilities.GrayModeApplier);
        DataContext = ViewModel;

        SourceCountList.ItemsSource = _sourceCounts;
        RecordList.ItemsSource = _recordItems;

        // 状态行（应用主题/清空/重新加载的结果）由 StatusMessage 是否有值决定显隐，
        // 绑定表达不了「null 就整行收起」，故订阅通知。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // WPF 的 IsVisibleChanged：外壳切页时重新拉一次记录（磁盘可能被别的进程改过）。
        PropertyChanged += OnControlPropertyChanged;

        // 先把提示摆成初始状态，避免出现一帧的错位。
        UpdateThemeCaption();
        UpdateProxyCustomVisibility();
        UpdateStatusVisibility();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 只初始化一次：页面在外壳里可能被反复卸载/挂载。
        Loaded -= OnLoaded;

        HookTheme();

        // Core 的 Load 是同步文件 IO，放后台线程；await 回到 UI 线程后再碰控件。
        try
        {
            await Task.Run(() => AppServices.Records.Load());
        }
        catch (Exception ex)
        {
            // async void 里漏出的异常会终止进程，整段包住。
            ViewModel.StatusMessage = ShellText.L("Settings.Records.ReadFailed", ex.Message);
        }

        RefreshRecordViews();
        UpdateThemeCaption();
        UpdateProxyCustomVisibility();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (!_themeHooked) return;

        AvaloniaThemeManager.ThemeChanged -= OnThemeChanged;
        _themeHooked = false;
    }

    private void HookTheme()
    {
        if (_themeHooked) return;

        AvaloniaThemeManager.ThemeChanged += OnThemeChanged;
        _themeHooked = true;
    }

    private void OnThemeChanged() => UpdateThemeCaption();

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;

        // 只在「刚变得可见」时刷新：不可见时刷新纯属浪费。
        if (IsVisible) RefreshRecordViews();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.StatusMessage))
        {
            UpdateStatusVisibility();
        }
    }

    // ===== 主题与语言：选中即应用 =====

    /// <summary>
    /// 主题族切换。
    ///
    /// <para>
    /// ViewModel 的 <c>SelectedTheme</c> 只是状态，真正的应用在 <c>ApplyThemeCommand</c> 里
    /// （它负责切字典 + 落盘 + 写 StatusMessage）。Avalonia 无触发器，故在此显式调用。
    /// </para>
    /// </summary>
    private void ThemeList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingTheme) return;

        ApplyThemeIfChanged();
    }

    /// <summary>
    /// 深浅色 / 跟随系统切换后重新应用主题。
    ///
    /// <para>
    /// 这两个开关的勾选由双向绑定写进 ViewModel，但 ViewModel 不会自己应用 ——
    /// 与 WPF 版一致，由视图在勾选后调用一次。
    /// </para>
    /// </summary>
    private void ApplyThemeIfChanged()
    {
        if (!ViewModel.ApplyThemeCommand.CanExecute(null)) return;

        // 置位期间忽略选中变更：应用主题可能连带改动主题列表的选中项
        // （例如「跟随系统」被系统深浅色切换带动），不加这层会绕回来再应用一次。
        _applyingTheme = true;
        try
        {
            ViewModel.ApplyThemeCommand.Execute(null);
        }
        finally
        {
            _applyingTheme = false;
        }

        UpdateThemeCaption();
    }

    /// <summary>语言下拉：选中即应用。</summary>
    private void LanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.ApplyLanguageCommand.CanExecute(null)) return;

        ViewModel.ApplyLanguageCommand.Execute(null);
    }

    /// <summary>
    /// 回显当前真正生效的 token 字典路径。
    ///
    /// <para>
    /// 这是「主题确实切过去了」的直接证据：主题族换字典时这里会跟着变。
    /// </para>
    /// </summary>
    private void UpdateThemeCaption()
    {
        CurrentThemeText.Text = AvaloniaThemeManager.ResolveRelativePath(
            AvaloniaThemeManager.CurrentKind, AvaloniaThemeManager.IsDark);
    }

    // ===== 代理 =====

    private void ProxyModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateProxyCustomVisibility();

    private void ProxyPasswordBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        ViewModel.ProxyPassword = ProxyPasswordBox.Text ?? string.Empty;

    /// <summary>只有「自定义」模式才显示地址/用户名/密码三行。</summary>
    private void UpdateProxyCustomVisibility()
    {
        var custom = ViewModel.SelectedProxyMode?.Mode == SettingsViewModel.ParseProxyMode("custom");
        ProxyCustomPanel.IsVisible = custom;
    }

    // ===== 记录管理 =====

    private async void Reload_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 同 OnLoaded：同步的 Core API 放后台线程，await 回到 UI 线程后再改界面。
            await Task.Run(() => AppServices.Records.Load());

            // 统计、按包源次数、明细一律从 Core 存储重新取，
            // 因此「重新加载」不需要重建 ViewModel，也不会让主题选中状态被重置。
            RefreshRecordViews();
            ViewModel.StatusMessage = ShellText.L("Settings.Records.Reloaded", AppServices.Records.TotalCount);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = ShellText.L("Settings.Records.ReloadFailed", ex.Message);
        }
    }

    /// <summary>
    /// 清空记录：先二次确认，确认后才执行。
    ///
    /// <para>
    /// 为什么确认必须挡在命令执行之前：清空是**不可撤销**的破坏性操作
    /// （Core 的 Clear 会立刻把空列表落盘，界面没有任何恢复途径）。
    /// WPF 版用 <c>ConfirmRequiredCommand</c> 把确认封进命令内部；Avalonia 侧按钮改为
    /// 直接挂 Click，确认逻辑写在这里 —— 语义等价（未确认绝不执行）。
    /// </para>
    /// </summary>
    private async void ClearRecords_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                ShellText.L("Settings.Records.ClearConfirm"),
                ShellText.L("Settings.Records.ClearCaption")))
        {
            return;
        }

        ViewModel.ClearRecordsCommand.Execute(null);
        RefreshRecordViews();   // 明细来自 Core 存储，视图侧重建一次
    }

    /// <summary>
    /// 重建整块「记录管理」的内容：统计数字、按包源次数、最近记录。
    ///
    /// <para>
    /// 数据源是 Core 的 <see cref="InstallRecordStore"/>（唯一权威），视图侧只做格式化、不缓存业务状态，
    /// 因此可以随时整块重取（首次显示、重新加载、清空记录之后各调一次）——
    /// 三条数据来自同一次读取，界面上不会出现「数字与明细对不上」。
    /// </para>
    /// </summary>
    private void RefreshRecordViews()
    {
        var store = AppServices.Records;

        // ---- 统计（总次数/成功/失败）----
        StatTotalText.Text = store.TotalCount.ToString(CultureInfo.InvariantCulture);
        StatSuccessText.Text = store.SuccessCount.ToString(CultureInfo.InvariantCulture);
        StatFailureText.Text = store.FailureCount.ToString(CultureInfo.InvariantCulture);

        // ---- 按包源的次数：降序；没用过的包源不显示（「0 次」的行没有信息量）----
        _sourceCounts.Clear();
        var counts = store.CountBySource
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.DisplayName(), StringComparer.Ordinal)
            .ToList();

        var max = counts.Count == 0 ? 0 : counts[0].Value;
        foreach (var (kind, count) in counts)
        {
            // 条形宽度按最大次数等比缩放，最少 MinimumBarWidth。
            var barWidth = max <= 0
                ? MinimumBarWidth
                : Math.Max(MinimumBarWidth, MaxBarWidth * count / max);
            _sourceCounts.Add(new SourceCountItem(
                kind.DisplayName(), count, ShellText.L("Settings.Records.Times", count), barWidth));
        }

        SourceCountEmpty.IsVisible = _sourceCounts.Count == 0;

        // ---- 最近记录：store.Records 本身就是「最近在前」，取 50 条够一屏翻看 ----
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
                ResultText: ShellText.L(item.Success ? "Record.Result.Success" : "Record.Result.Failure"),
                Duration: FormatDuration(item.DurationMs),
                ErrorText: errorCode.Length == 0 ? string.Empty : ShellText.L("Record.Detail.ErrorCode", errorCode),
                HasError: errorCode.Length > 0));
        }

        RecordEmpty.IsVisible = _recordItems.Count == 0;

        // 记录文件位置：排查「记录去哪了」最常需要的一句话信息。
        RecordStorePathText.Text = ShellText.L("Home.RecordFile", store.StorePath);
        ToolTip.SetTip(RecordStorePathText, store.StorePath);
    }

    /// <summary>状态行：StatusMessage 为空时整行收起（避免占一条空行）。</summary>
    private void UpdateStatusVisibility()
    {
        var hasMessage = !string.IsNullOrWhiteSpace(ViewModel.StatusMessage);

        StatusText.IsVisible = hasMessage;
        StatusText.Text = ViewModel.StatusMessage ?? string.Empty;
    }

    /// <summary>操作类型中文化。未知值原样显示：Core 以后新增操作类型时界面既不会空白也不会崩。</summary>
    private static string ActionDisplayName(string action) => action switch
    {
        "install" => ShellText.L("Record.Op.Install"),
        "uninstall" => ShellText.L("Record.Op.Uninstall"),
        "upgrade" => ShellText.L("Record.Op.Upgrade"),
        _ => string.IsNullOrWhiteSpace(action) ? "—" : action,
    };

    /// <summary>
    /// 耗时可读化：不足 1 秒用毫秒，超过 1 分钟用「分 秒」，其余用秒（两位小数）。
    /// 0（Core 表示未记录到耗时）显示成破折号，避免被误读成「瞬间完成」。
    /// </summary>
    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0) return "—";
        if (milliseconds < 1000) return $"{milliseconds} ms";

        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalMinutes >= 1
            ? ShellText.L("Settings.Records.Duration", (int)span.TotalMinutes, span.Seconds)
            : ShellText.L("Settings.Records.DurationSeconds",
                span.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture));
    }

    // ===== 二次确认（Avalonia 无内置 MessageBox） =====

    /// <summary>
    /// 弹一个模态确认框，返回用户是否点了「确定」。
    ///
    /// <para>
    /// 为什么自建而不是调第三方：只有一处需要确认，引入对话框库不值得；
    /// 而且这个框必须跟随主题 token，第三方默认外观会与设置页的卡片格格不入。
    /// 无宿主窗口（设计器、单测）时直接返回 false —— 宁可不动数据，也不要在无人确认的情况下清空。
    /// </para>
    /// </summary>
    private async Task<bool> ConfirmAsync(string message, string caption)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return false;

        var dialog = new Window
        {
            Title = caption,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ResolveBrush("BrCard") ?? Brushes.White,
            ShowInTaskbar = false,
        };

        var cancel = new Button
        {
            Content = ShellText.L("Common.Cancel"),
            MinWidth = 80,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancel.Classes.Add("pcl-secondary");

        var ok = new Button
        {
            Content = ShellText.L("Common.Ok"),
            MinWidth = 80,
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ok.Classes.Add("pcl-primary");

        cancel.Click += (_, _) => dialog.Close(false);
        ok.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = ResolveBrush("BrG1") ?? Brushes.Black,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>按 token 名取画刷（取不到时返回 null，由调用方给兜底色）。</summary>
    private static IBrush? ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : null;
}
