using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.App.Themes;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Core.Terminal;

namespace UMango.App.ViewModels;

/// <summary>设置页里一个可选项：主题族（深浅由独立开关决定）。</summary>
public sealed record ThemeOption(ThemeKind Kind, string Title, string Description);

/// <summary>
/// 设置页：主题与外观（PCL 经典 / Windows 10 Fluent × 浅/深）、调试模式、记录管理。
///
/// 两点设计约定：
///   · 主题切换**不在本类实现**，只调 <see cref="ThemeManager"/>（唯一权威），
///     否则"谁最后设置的颜色算数"会变成两次实现互相打架；
///   · 记录统计是**内存快照**（构造与清空时刷新），不在属性 getter 里去读磁盘/排序，
///     绑定是每帧都可能求值的，getter 里做重活会直接拖住渲染。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>可选主题。深浅是独立开关，所以这里只有两族。</summary>
    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new ThemeOption(ThemeKind.PclClassic, "PCL 经典",
            "保留现有 PCL 设计数值，界面观感与录屏逐像素校验一致"),
        new ThemeOption(ThemeKind.Fluent10, "Windows 10 Fluent",
            "还原 Win10「设置」的方正紧凑风格（纯色标题栏、小圆角，非 Win11/WinUI3）")
    ];

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private bool _isDark;

    /// <summary>中性反馈（应用主题、清空记录的结果）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    // ---- 记录统计的内存快照（由 RefreshRecords 刷新）----
    private int _totalRecords;
    private int _successCount;
    private int _failureCount;
    private IReadOnlyDictionary<PackageSourceKind, int> _countBySource = new Dictionary<PackageSourceKind, int>();
    private IReadOnlyList<InstallRecord> _recentRecords = [];

    public int TotalRecords => _totalRecords;
    public int SuccessCount => _successCount;
    public int FailureCount => _failureCount;

    /// <summary>按包源的次数记录（各分类旁边显示用过多少次）。</summary>
    public IReadOnlyDictionary<PackageSourceKind, int> CountBySource => _countBySource;

    /// <summary>最近的管理记录（新的在前），只取一屏够用的条数。</summary>
    public IReadOnlyList<InstallRecord> RecentRecords => _recentRecords;

    /// <summary>
    /// 调试模式。刻意手写属性而不是 [ObservableProperty]：
    /// getter 直接读 Core 的静态开关（<see cref="TerminalBridge.DebugMode"/>）而不是内存副本 ——
    /// 进度页也能改这个开关，读副本会出现"两个页面显示的状态不一致"。
    /// </summary>
    public bool DebugMode
    {
        get => TerminalBridge.DebugMode;
        set
        {
            if (TerminalBridge.DebugMode == value) return;
            AppSettings.DebugMode = value;   // 写穿到 Core 并持久化（下次启动仍是用户选的）
            OnPropertyChanged();
        }
    }

    public SettingsViewModel()
    {
        var (kind, dark) = ReadCurrentTheme();
        _selectedTheme = Themes.FirstOrDefault(t => t.Kind == kind) ?? Themes[0];
        _isDark = dark;

        // 主题可能被别处（快捷键/其它页面）切换：跟着走，
        // 否则设置页的选中项会与实际观感脱节。
        ThemeManager.ThemeChanged += SyncFromThemeManager;

        RefreshRecords();
    }

    /// <summary>应用所选主题（PCL 经典 / Win10 Fluent × 浅/深）。</summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        try
        {
            var option = SelectedTheme ?? Themes[0];
            ThemeManager.Apply(option.Kind, IsDark);
            StatusMessage = $"已应用主题：{option.Title}（{(IsDark ? "深色" : "浅色")}）。";
        }
        catch (Exception ex)
        {
            // 主题字典缺失/路径写错不该让设置页崩掉：给出可读原因即可。
            // 注意 Apply 内部会触发 ThemeChanged，若它在半途失败，界面可能停在旧主题上，
            // 所以这里必须把"失败了"明确说出来，而不是假装成功。
            StatusMessage = $"主题切换失败：{ex.Message}";
        }
    }

    /// <summary>清空管理记录与次数记录（不可撤销）。</summary>
    [RelayCommand]
    private void ClearRecords()
    {
        try
        {
            AppServices.Records.Clear();
            RefreshRecords();
            StatusMessage = "已清空全部管理记录与次数记录。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"清空记录失败：{ex.Message}";
        }
    }

    /// <summary>把界面状态对齐到 ThemeManager 的当前值（不反向调用 Apply，避免递归）。</summary>
    private void SyncFromThemeManager()
    {
        var (kind, dark) = ReadCurrentTheme();
        SelectedTheme = Themes.FirstOrDefault(t => t.Kind == kind) ?? Themes[0];
        IsDark = dark;
    }

    /// <summary>
    /// 主题状态的唯一权威是 <see cref="ThemeManager"/>。
    /// 对它的读取集中在这里一处：将来 ThemeManager 调整签名时只需改这一行，
    /// 不会散落在多个属性里。
    /// </summary>
    private static (ThemeKind Kind, bool Dark) ReadCurrentTheme() =>
        (ThemeManager.CurrentKind, ThemeManager.IsDark);

    /// <summary>重建记录统计快照并通知界面。</summary>
    private void RefreshRecords()
    {
        var store = AppServices.Records;

        _totalRecords = store.TotalCount;
        _successCount = store.SuccessCount;
        _failureCount = store.FailureCount;
        _countBySource = store.CountBySource;

        // 只有一屏用得上的最近记录：store.Records 每次访问都会排序，
        // 这里取一次留作快照，避免绑定反复触发排序。
        _recentRecords = store.Records.Take(50).ToList();

        OnPropertyChanged(nameof(TotalRecords));
        OnPropertyChanged(nameof(SuccessCount));
        OnPropertyChanged(nameof(FailureCount));
        OnPropertyChanged(nameof(CountBySource));
        OnPropertyChanged(nameof(RecentRecords));
    }
}
