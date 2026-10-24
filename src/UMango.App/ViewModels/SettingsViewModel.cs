using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.App.Themes;
using UMango.Core.Animation;
using UMango.Core.Configuration;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Core.Terminal;
using UMango.Core.Network;

using UMango.App.Localization;

namespace UMango.App.ViewModels;

/// <summary>设置页里一个可选项：主题族（深浅由独立开关决定）。</summary>
public sealed record ThemeOption(ThemeKind Kind, string Title, string Description);

/// <summary>动画方向选项（需求要求两种矢量都可选，所以两个都列出来）</summary>
public sealed record AnimationDirectionOption(
    AnimationDirectionMode Mode, string Title, string Description);

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
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    /// <summary>
    /// 检查更新卡片的 VM。与主题/动画这些"改完即生效"的设置项不同，
    /// 更新有检查→下载→重启的完整生命周期，独立成一个 VM 更清楚。
    /// </summary>
    public UpdateViewModel Update { get; } = new();

    // ==================== 网络代理 ====================

    /// <summary>代理模式的可选项。</summary>
    public IReadOnlyList<ProxyModeOption> ProxyModes { get; } =
    [
        new(ProxyMode.NoProxy, LocalizationService.Text("Settings.Proxy.Mode.None")),
        new(ProxyMode.SystemProxy, LocalizationService.Text("Settings.Proxy.Mode.System")),
        new(ProxyMode.CustomProxy, LocalizationService.Text("Settings.Proxy.Mode.Custom")),
    ];

    /// <summary>代理模式选项。</summary>
    public sealed record ProxyModeOption(ProxyMode Mode, string DisplayName);

    private ProxyModeOption? _selectedProxyMode;

    /// <summary>当前代理模式。</summary>
    public ProxyModeOption SelectedProxyMode
    {
        get => _selectedProxyMode ??= ProxyModes[0];
        set
        {
            if (value is null || value == _selectedProxyMode)
                return;
            _selectedProxyMode = value;
            OnPropertyChanged(nameof(SelectedProxyMode));
            ApplyProxySettings();
        }
    }

    private string _proxyAddress = "";
    private string _proxyUsername = "";
    private string _proxyPassword = "";

    /// <summary>自定义代理地址。</summary>
    public string ProxyAddress
    {
        get => _proxyAddress;
        set
        {
            if (value == _proxyAddress) return;
            _proxyAddress = value;
            OnPropertyChanged(nameof(ProxyAddress));
            ApplyProxySettings();
        }
    }

    /// <summary>自定义代理用户名。</summary>
    public string ProxyUsername
    {
        get => _proxyUsername;
        set
        {
            if (value == _proxyUsername) return;
            _proxyUsername = value;
            OnPropertyChanged(nameof(ProxyUsername));
            ApplyProxySettings();
        }
    }

    /// <summary>自定义代理密码（只写不读，避免密码出现在绑定回显里）。</summary>
    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            if (value == _proxyPassword) return;
            _proxyPassword = value;
            ApplyProxySettings();
        }
    }

    private string _proxyStatusMessage = "";

    /// <summary>系统代理探测状态。</summary>
    public string ProxyStatusMessage
    {
        get => _proxyStatusMessage;
        private set
        {
            if (value == _proxyStatusMessage) return;
            _proxyStatusMessage = value;
            OnPropertyChanged(nameof(ProxyStatusMessage));
        }
    }


    /// <summary>
    /// 灰度（哀悼日）模式开关。写到 <see cref="GrayModeController.GrayModeKey"/> 是唯一权威；
    /// 控制器本身挂在 <c>App</c> 上并订阅 <c>ThemeManager.ThemeChanged</c>，
    /// 所以这里只管设置项与界面状态，不必自己施加特效。
    /// </summary>
    public bool GrayMode
    {
        get => AppSettings.Settings.Get<bool>(UMango.App.Themes.GrayModeController.GrayModeKey);
        set
        {
            if (value == GrayMode) return;
            AppSettings.Settings.Set(UMango.App.Themes.GrayModeController.GrayModeKey, value);
            AppSettings.Save();

            // 立即施加，而不是只写设置项等下次启动：开关类设置项的反馈必须是即时的。
            // 无 UI 宿主（单元测试）里 Gray 为 null，此时只落盘。
            App.Gray?.SetEnabled(value, App.GrayResources);
            OnPropertyChanged(nameof(GrayMode));
        }
    }

    /// <summary>
    /// 深浅色是否跟随操作系统。
    ///
    /// 勾上后，界面深浅由系统设置决定（Windows 看注册表、macOS 看
    /// AppleInterfaceStyle、Linux 看 color-scheme），手动开关不再生效。
    /// </summary>
    public bool FollowSystem
    {
        get => AppSettings.Settings.Get<string>(UiSettings.ThemeFollowKey) is { Length: > 0 } stored
            ? UMango.Core.UI.Theme.SystemThemeResolver.FromStorageValue(stored) == UMango.Core.UI.Theme.ThemeFollowMode.System
            : true;
        set
        {
            var mode = value ? UMango.Core.UI.Theme.ThemeFollowMode.System : UMango.Core.UI.Theme.ThemeFollowMode.Light;
            AppSettings.Settings.Set(UiSettings.ThemeFollowKey, UMango.Core.UI.Theme.SystemThemeResolver.ToStorageValue(mode));
            AppSettings.Save();
            OnPropertyChanged(nameof(FollowSystem));
        }
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new ThemeOption(ThemeKind.PclClassic, L("Settings.Theme.PclName"),
            L("Settings.Theme.PclDesc")),
        new ThemeOption(ThemeKind.Fluent10, L("Settings.Theme.Fluent10Name"),
            L("Settings.Theme.Fluent10Desc"))
    ];

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    /// <summary>动画方向选项：UMango 自有矢量 / 与 PCL-CE 一致</summary>
    public IReadOnlyList<AnimationDirectionOption> AnimationDirections { get; } =
    [
        new AnimationDirectionOption(AnimationDirectionMode.Mirrored, L("Settings.Anim.MirroredName"),
            L("Settings.Anim.MirroredDesc")),
        new AnimationDirectionOption(AnimationDirectionMode.PclCompatible, L("Settings.Anim.PclCompatibleName"),
            L("Settings.Anim.PclCompatibleDesc"))
    ];

    /// <summary>
    /// 是否启用动画。刻意手写而不是 [ObservableProperty]：
    /// 要把值写回 AppSettings（唯一权威）并落盘，不能只改一份内存副本。
    /// </summary>
    public bool AnimationEnabled
    {
        get => AppSettings.Settings.Get<bool>(UiSettings.AnimationEnabledKey);
        set
        {
            if (AnimationEnabled == value) return;
            AppSettings.Settings.Set(UiSettings.AnimationEnabledKey, value);
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>动画方向（切换后全局生效，对应需求"暴露为全局可切换设置"）</summary>
    public AnimationDirectionOption SelectedAnimationDirection
    {
        get
        {
            var mode = AppSettings.Settings.Get<AnimationDirectionMode>(UiSettings.AnimationDirectionKey);
            return AnimationDirections.FirstOrDefault(o => o.Mode == mode) ?? AnimationDirections[0];
        }
        set
        {
            if (value is null) return;
            if (AppSettings.Settings.Get<AnimationDirectionMode>(UiSettings.AnimationDirectionKey) == value.Mode) return;

            AppSettings.Settings.Set(UiSettings.AnimationDirectionKey, value.Mode);
            AppSettings.Save();
            OnPropertyChanged();
            AnimationDirectionChanged?.Invoke();
        }
    }

    /// <summary>方向切换后触发，供页面重放一次动画让用户立刻看到效果</summary>
    public event Action? AnimationDirectionChanged;

    /// <summary>动画速度倍率（0.5 慢放 ~ 2.0 加速）</summary>
    public double AnimationSpeedFactor
    {
        get => AppSettings.Settings.Get<double>(UiSettings.AnimationSpeedKey);
        set
        {
            // 量程与描述符里的 Min/Max 一致；越界由设置服务拒绝（这里先夹一次，避免滑块抖动时报错）
            var clamped = Math.Clamp(value, 0.5, 2.0);
            if (Math.Abs(AnimationSpeedFactor - clamped) < 0.001) return;
            AppSettings.Settings.Set(UiSettings.AnimationSpeedKey, clamped);
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

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

    private string _announcementSource = "";
    private string _announcementStatusMessage = "";

    /// <summary>
    /// 公告源地址（留空 = 不抓取公告）。
    ///
    /// 写穿到 Core 的设置服务（唯一权威），落盘后下次启动仍在。
    ///
    /// **为什么带一个内存副本**：设置服务是唯一权威，但界面需要一个"上一次输入的值"
    /// 来判断"用户是不是又打了同样的字"（同样的字不该反复落盘、反复刷提示）。
    /// 这个副本在构造时从设置服务初始化，所以它和权威值一开始是一致的；
    /// 若不初始化，"清空输入框"会撞上"值没变"的短路分支而**永远存不进去** ——
    /// 那正是"把公告源删掉"这个最常见的操作失效。
    /// </summary>
    public string AnnouncementSource
    {
        get => _announcementSource;
        set
        {
            if (value == _announcementSource) return;
            _announcementSource = value;
            OnPropertyChanged(nameof(AnnouncementSource));
            ApplyAnnouncementSource();
        }
    }

    /// <summary>公告源的状态提示（例如"留空表示不抓取"）。</summary>
    public string AnnouncementStatusMessage
    {
        get => _announcementStatusMessage;
        private set
        {
            if (value == _announcementStatusMessage) return;
            _announcementStatusMessage = value;
            OnPropertyChanged(nameof(AnnouncementStatusMessage));
        }
    }

    private void ApplyAnnouncementSource()
    {
        try
        {
            AppSettings.Settings.Set(UiSettings.AnnouncementSourceKey, _announcementSource.Trim());
            AppSettings.Save();
            AnnouncementStatusMessage = string.IsNullOrWhiteSpace(_announcementSource)
                ? ViewText.L("Announcement.NoSource")
                : ViewText.L("Settings.Announcement.Saved");
        }
        catch (Exception ex)
        {
            AnnouncementStatusMessage = ViewText.L("Announcement.Failed", ex.Message);
        }
    }

    /// <summary>语言选项（首项 = 跟随系统）。</summary>
    public IReadOnlyList<AppLocalization.LanguageOption> Languages { get; } =
        AppLocalization.BuildOptions();

    [ObservableProperty]
    private AppLocalization.LanguageOption? _selectedLanguage;

    public SettingsViewModel()
    {
        var (kind, dark) = ReadCurrentTheme();
        _selectedTheme = Themes.FirstOrDefault(t => t.Kind == kind) ?? Themes[0];
        _isDark = dark;

        // 回显已保存的公告源（否则输入框是空的，用户会以为没保存过）。
        AppSettings.Load();
        _announcementSource = AppSettings.Settings.Get<string>(UiSettings.AnnouncementSourceKey);

        // 回显当前生效的语言（保存值 → 解析成选项；解析不到时回落"跟随系统"）
        var savedCode = AppLocalization.ReadSavedLanguageCode();
        _selectedLanguage = Languages.FirstOrDefault(o => o.Code == savedCode) ?? Languages[0];

        // 主题可能被别处（快捷键/其它页面）切换：跟着走，
        // 否则设置页的选中项会与实际观感脱节。
        ThemeManager.ThemeChanged += SyncFromThemeManager;

        // 代理：回显保存的模式与地址，并把代理管理器接到网络层
        _selectedProxyMode = ProxyModes.FirstOrDefault(m => m.Mode == AppServices.ProxyManager.Mode) ?? ProxyModes[0];
        _proxyAddress = AppServices.ProxyManager.CustomProxyAddress?.ToString() ?? "";
        ApplyProxySettings();

        RefreshRecords();
    }

    /// <summary>
    /// 把代理设置应用到代理管理器，并让网络层重建客户端。
    /// </summary>
    private void ApplyProxySettings()
    {
        var manager = AppServices.ProxyManager;
        manager.Mode = (SelectedProxyMode ?? ProxyModes[0]).Mode;

        if (manager.Mode == ProxyMode.CustomProxy)
        {
            if (!string.IsNullOrWhiteSpace(ProxyAddress)
                && Uri.TryCreate(ProxyAddress, UriKind.Absolute, out var uri))
            {
                manager.CustomProxyAddress = uri;
            }
            manager.CustomProxyCredentials = string.IsNullOrEmpty(ProxyUsername)
                ? null
                : new System.Net.NetworkCredential(ProxyUsername, ProxyPassword);
        }
        else if (manager.Mode == ProxyMode.SystemProxy)
        {
            manager.RefreshSystemProxy();
        }

        AppServices.Http.RefreshProxy();
        UpdateProxyStatus();
    }

    /// <summary>更新「系统代理探测状态」那一行。</summary>
    private void UpdateProxyStatus()
    {
        var manager = AppServices.ProxyManager;
        ProxyStatusMessage = manager.Mode switch
        {
            ProxyMode.NoProxy => L("Settings.Proxy.Status.None"),
            ProxyMode.CustomProxy => L("Settings.Proxy.Status.Custom", manager.CustomProxyAddress?.ToString() ?? "-"),
            _ => manager.SystemProxyAvailable
                ? L("Settings.Proxy.Status.SystemFound", manager.GetProxyUrl() ?? "-")
                : L("Settings.Proxy.Status.SystemNone"),
        };
    }

    /// <summary>切换语言（选中即应用，与主题族一致）。</summary>
    [RelayCommand]
    private void ApplyLanguage()
    {
        var option = SelectedLanguage ?? Languages[0];
        AppLocalization.SetLanguage(option.Code);
        StatusMessage = L("Settings.Language.Switched", option.DisplayName);
    }

    /// <summary>应用所选主题（PCL 经典 / Win10 Fluent × 浅/深）。</summary>
    [RelayCommand]
    private void ApplyTheme()
    {
        try
        {
            var option = SelectedTheme ?? Themes[0];
            if (FollowSystem)
            {
                // 跟随系统：深浅色由系统决定，用户选的 IsDark 只是回显。
                ThemeManager.ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode.System);
            }
            else
            {
                ThemeManager.Apply(option.Kind, IsDark);
            }

            // 持久化：否则重启后又回到默认主题（之前审计发现的缺口）
            AppSettings.Settings.Set(UiSettings.ThemeKindKey,
                option.Kind == ThemeKind.Fluent10 ? ThemeFamily.Fluent10 : ThemeFamily.PclClassic);
            AppSettings.Settings.Set(UiSettings.DarkModeKey, IsDark);
            AppSettings.Save();

            StatusMessage = L("Settings.Theme.Applied", option.Title, L(IsDark ? "Settings.Dark" : "Settings.Light"));
        }
        catch (Exception ex)
        {
            // 主题字典缺失/路径写错不该让设置页崩掉：给出可读原因即可。
            // 注意 Apply 内部会触发 ThemeChanged，若它在半途失败，界面可能停在旧主题上，
            // 所以这里必须把"失败了"明确说出来，而不是假装成功。
            StatusMessage = L("Settings.Theme.SwitchFailed", ex.Message);
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
            StatusMessage = L("Settings.Records.Cleared");
        }
        catch (Exception ex)
        {
            StatusMessage = L("Settings.Records.ClearFailed", ex.Message);
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
