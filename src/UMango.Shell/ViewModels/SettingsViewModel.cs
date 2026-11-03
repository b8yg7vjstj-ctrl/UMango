using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.Animation;
using UMango.Core.Configuration;
using UMango.Core.Localization;
using UMango.Core.Media.Background;
using UMango.Core.Network;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Core.Terminal;
using UMango.Shell.Capabilities;
using UMango.Shell.Localization;
using UMango.Shell.Services;
using UMango.Shell.Theming;
using ThemeKind = UMango.Shell.Theming.ThemeKind;

namespace UMango.Shell.ViewModels;

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
    private readonly IGrayModeApplier? _grayApplier;

    /// <summary>可选主题。深浅是独立开关，所以这里只有两族。</summary>


    /// <summary>
    /// 解析代理模式（与 WPF 侧 <c>SetupObserver.ParseProxyMode</c> 同一份语义）。
    ///
    /// <para>
    /// 刻意在这里保留一份而**不**引用 WPF 侧的 <c>SetupObserver</c>：那个类在
    /// <c>UMango.App</c>（WPF 工程）里，Shell 层引用它就等于把 WPF 拖回来。
    /// 代价是两处要同步 —— 因此两边都用同一组测试锁住行为。
    /// </para>
    /// </summary>
    public static ProxyMode ParseProxyMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "system" => ProxyMode.SystemProxy,
        "custom" => ProxyMode.CustomProxy,
        _ => ProxyMode.NoProxy,
    };

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

    // ==================== DoH ====================

    private string _dohUpstreams = "";
    private int _dohCacheTtl = 300;
    private int _dohTimeout = 5000;
    private string _dohStatusMessage = "";

    /// <summary>
    /// DoH 上游地址（逗号分隔；留空 = 不启用 DoH，走系统 DNS）。
    ///
    /// 写穿到设置服务后，<c>AppServices</c> 订阅的变更事件会立刻把新配置套到解析器上
    /// 并清空缓存（见 <c>AppServices.CreateService</c> 里的 DoH 分派），
    /// 所以这里不必自己去碰解析器。
    /// </summary>
    public string DohUpstreams
    {
        get => _dohUpstreams;
        set
        {
            if (value == _dohUpstreams) return;
            _dohUpstreams = value;
            OnPropertyChanged(nameof(DohUpstreams));
            ApplyDohSettings();
        }
    }

    /// <summary>DoH 缓存生存时间（秒）。</summary>
    public int DohCacheTtl
    {
        get => _dohCacheTtl;
        set
        {
            var clamped = Math.Clamp(value, 0, 86400);
            if (clamped == _dohCacheTtl) return;
            _dohCacheTtl = clamped;
            OnPropertyChanged(nameof(DohCacheTtl));
            ApplyDohSettings();
        }
    }

    /// <summary>DoH 查询超时（毫秒）。</summary>
    public int DohTimeout
    {
        get => _dohTimeout;
        set
        {
            var clamped = Math.Clamp(value, 200, 60000);
            if (clamped == _dohTimeout) return;
            _dohTimeout = clamped;
            OnPropertyChanged(nameof(DohTimeout));
            ApplyDohSettings();
        }
    }

    /// <summary>DoH 当前状态提示（启用 / 未启用 / 保存失败）。</summary>
    public string DohStatusMessage
    {
        get => _dohStatusMessage;
        private set
        {
            if (value == _dohStatusMessage) return;
            _dohStatusMessage = value;
            OnPropertyChanged(nameof(DohStatusMessage));
        }
    }

    private void ApplyDohSettings()
    {
        try
        {
            var settings = AppSettings.Settings;
            settings.Set(UiSettings.DohUpstreamsKey, _dohUpstreams.Trim());
            settings.Set(UiSettings.DohCacheTtlKey, _dohCacheTtl);
            settings.Set(UiSettings.DohTimeoutKey, _dohTimeout);
            AppSettings.Save();

            // 设置变更事件已经会重套一次配置，这里再显式调一次是为了覆盖
            // "值与已保存值相同"这条短路分支（那种情况下不会触发变更事件）。
            AppServices.RefreshDnsSettings();

            DohStatusMessage = string.IsNullOrWhiteSpace(_dohUpstreams)
                ? ShellText.L("Settings.Doh.Status.Disabled")
                : ShellText.L("Settings.Doh.Status.Enabled");
        }
        catch (Exception ex)
        {
            DohStatusMessage = ShellText.L("Settings.Doh.Failed", ex.Message);
        }
    }


    /// <summary>
    /// 灰度（哀悼日）模式开关。写到 <see cref="GrayModeController.GrayModeKey"/> 是唯一权威；
    /// 控制器本身挂在 <c>App</c> 上并订阅 <c>ThemeManager.ThemeChanged</c>，
    /// 所以这里只管设置项与界面状态，不必自己施加特效。
    /// </summary>
    public bool GrayMode
    {
        get => AppSettings.Settings.Get<bool>(UiSettings.GrayModeKey);
        set
        {
            if (value == GrayMode) return;
            AppSettings.Settings.Set(UiSettings.GrayModeKey, value);
            AppSettings.Save();

            // 立即施加，而不是只写设置项等下次启动：开关类设置项的反馈必须是即时的。
            // 无 UI 宿主（单元测试）里 Gray 为 null，此时只落盘。
            _grayApplier?.SetEnabled(value);
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
        new ThemeOption(ThemeKind.PclClassic, ShellText.L("Settings.Theme.PclName"),
            ShellText.L("Settings.Theme.PclDesc")),
        new ThemeOption(ThemeKind.Fluent10, ShellText.L("Settings.Theme.Fluent10Name"),
            ShellText.L("Settings.Theme.Fluent10Desc"))
    ];

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    /// <summary>动画方向选项：UMango 自有矢量 / 与 PCL-CE 一致</summary>
    public IReadOnlyList<AnimationDirectionOption> AnimationDirections { get; } =
    [
        new AnimationDirectionOption(AnimationDirectionMode.Mirrored, ShellText.L("Settings.Anim.MirroredName"),
            ShellText.L("Settings.Anim.MirroredDesc")),
        new AnimationDirectionOption(AnimationDirectionMode.PclCompatible, ShellText.L("Settings.Anim.PclCompatibleName"),
            ShellText.L("Settings.Anim.PclCompatibleDesc"))
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

    // ==================== 行为 ====================

    /// <summary>
    /// 是否显示当前平台不支持的包源。
    ///
    /// 与下载页的左栏开关是**同一个键**（<see cref="UiSettings.ShowUnsupportedSourcesKey"/>）：
    /// 两处都必须写穿到设置服务并落盘，否则会出现"设置页开了、下载页还是隐藏"的错位。
    /// </summary>
    public bool ShowUnsupportedSources
    {
        get => AppSettings.Settings.Get<bool>(UiSettings.ShowUnsupportedSourcesKey);
        set
        {
            if (ShowUnsupportedSources == value) return;
            AppSettings.Settings.Set(UiSettings.ShowUnsupportedSourcesKey, value);
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 安装前二次确认。关闭后点「安装」会直接执行（见 <c>DownloadViewModel.ConfirmInstall</c>）。
    /// </summary>
    public bool ConfirmBeforeInstall
    {
        get => AppSettings.Settings.Get<bool>(UiSettings.ConfirmBeforeInstallKey);
        set
        {
            if (ConfirmBeforeInstall == value) return;
            AppSettings.Settings.Set(UiSettings.ConfirmBeforeInstallKey, value);
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

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

    /// <summary>
    /// 日志页在内存里保留的最大条数。
    ///
    /// 写穿到 Core 的设置服务；Core 侧的 <c>MemoryLogSink.Capacity</c> 会在设置变更时
    /// 立刻裁剪到新容量（调小时丢弃最旧的条目，计入「已丢弃」计数）。
    ///
    /// 对应 PCL-CE 的「最大日志行数」滑块，但 PCL 的滑块值是要经公式换算的档位
    /// （≤5 → ×10+50 …），UMango 直接用可读的条数。
    /// </summary>
    public int LogBufferCapacity
    {
        get => AppSettings.Settings.Get<int>(UiSettings.LogBufferCapacityKey);
        set
        {
            var clamped = Math.Clamp(value, 200, 50000);
            if (LogBufferCapacity == clamped) return;
            AppSettings.Settings.Set(UiSettings.LogBufferCapacityKey, clamped);
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
                ? ShellText.L("Announcement.NoSource")
                : ShellText.L("Settings.Announcement.Saved");
        }
        catch (Exception ex)
        {
            AnnouncementStatusMessage = ShellText.L("Announcement.Failed", ex.Message);
        }
    }

    // ---------------- 窗口外观 ----------------

    private int _windowOpacity = 1000;
    private bool _lockWindowSize;
    private string _uiFont = "";
    private string _titleBarText = "";
    private bool _titleBarLeftAlign;

    /// <summary>窗口不透明度（千分比，1000 = 完全不透明）。</summary>
    public int WindowOpacity
    {
        get => _windowOpacity;
        set
        {
            var clamped = Math.Clamp(value, 200, 1000);
            if (clamped == _windowOpacity) return;
            _windowOpacity = clamped;
            OnPropertyChanged(nameof(WindowOpacity));
            PersistAppearanceSettings();
        }
    }

    /// <summary>锁定窗口大小。</summary>
    public bool LockWindowSize
    {
        get => _lockWindowSize;
        set
        {
            if (value == _lockWindowSize) return;
            _lockWindowSize = value;
            OnPropertyChanged(nameof(LockWindowSize));
            PersistAppearanceSettings();
        }
    }

    /// <summary>界面字体（留空 = 主题默认）。</summary>
    public string UiFont
    {
        get => _uiFont;
        set
        {
            if (value == _uiFont) return;
            _uiFont = value;
            OnPropertyChanged(nameof(UiFont));
            PersistAppearanceSettings();
        }
    }

    /// <summary>标题栏文字（留空 = 应用名）。</summary>
    public string TitleBarText
    {
        get => _titleBarText;
        set
        {
            if (value == _titleBarText) return;
            _titleBarText = value;
            OnPropertyChanged(nameof(TitleBarText));
            PersistAppearanceSettings();
        }
    }

    /// <summary>标题栏选项卡左对齐。</summary>
    public bool TitleBarLeftAlign
    {
        get => _titleBarLeftAlign;
        set
        {
            if (value == _titleBarLeftAlign) return;
            _titleBarLeftAlign = value;
            OnPropertyChanged(nameof(TitleBarLeftAlign));
            PersistAppearanceSettings();
        }
    }

    /// <summary>把窗口外观相关设置写回设置服务（观察者会据此重应用）。</summary>
    private void PersistAppearanceSettings()
    {
        try
        {
            var settings = AppSettings.Settings;
            settings.Set(UiSettings.WindowOpacityKey, _windowOpacity);
            settings.Set(UiSettings.LockWindowSizeKey, _lockWindowSize);
            settings.Set(UiSettings.FontKey, _uiFont);
            settings.Set(UiSettings.TitleBarTextKey, _titleBarText);
            settings.Set(UiSettings.TitleBarLeftAlignKey, _titleBarLeftAlign);
            AppSettings.Save();
        }
        catch (Exception)
        {
            // 落盘失败不该打断界面操作。
        }
    }

    // ---------------- 下载限额 ----------------

    private int _downloadThreadLimit = 8;
    private int _downloadFileConnectionLimit = 4;
    private int _downloadSpeedLimit;

    /// <summary>全局最大连接数。</summary>
    public int DownloadThreadLimit
    {
        get => _downloadThreadLimit;
        set
        {
            var clamped = Math.Clamp(value, 1, 64);
            if (clamped == _downloadThreadLimit) return;
            _downloadThreadLimit = clamped;
            OnPropertyChanged(nameof(DownloadThreadLimit));
            PersistDownloadSettings();
        }
    }

    /// <summary>单主机最大连接数。</summary>
    public int DownloadFileConnectionLimit
    {
        get => _downloadFileConnectionLimit;
        set
        {
            var clamped = Math.Clamp(value, 1, 64);
            if (clamped == _downloadFileConnectionLimit) return;
            _downloadFileConnectionLimit = clamped;
            OnPropertyChanged(nameof(DownloadFileConnectionLimit));
            PersistDownloadSettings();
        }
    }

    /// <summary>全局限速（KB/秒，0 = 不限速）。</summary>
    public int DownloadSpeedLimit
    {
        get => _downloadSpeedLimit;
        set
        {
            var clamped = Math.Max(0, value);
            if (clamped == _downloadSpeedLimit) return;
            _downloadSpeedLimit = clamped;
            OnPropertyChanged(nameof(DownloadSpeedLimit));
            PersistDownloadSettings();
        }
    }

    /// <summary>把下载限额写回设置服务。</summary>
    private void PersistDownloadSettings()
    {
        try
        {
            var settings = AppSettings.Settings;
            settings.Set(UiSettings.DownloadThreadLimitKey, _downloadThreadLimit);
            settings.Set(UiSettings.DownloadFileConnectionLimitKey, _downloadFileConnectionLimit);
            settings.Set(UiSettings.DownloadSpeedLimitKey, _downloadSpeedLimit);
            AppSettings.Save();
        }
        catch (Exception)
        {
            // 落盘失败不该打断界面操作。
        }
    }

    // ---------------- 动态背景 ----------------

    private string _backgroundDirectory = "";
    private bool _backgroundEnabled;
    private bool _backgroundVideoEnabled;
    private bool _backgroundForcePlay;
    private int _backgroundOpacity = 1000;
    private int _backgroundBlur;
    private BackgroundSuitMode _backgroundSuit = BackgroundSuitMode.Smart;
    private string _backgroundStatusMessage = "";

    /// <summary>动态背景是否启用。</summary>
    public bool BackgroundEnabled
    {
        get => _backgroundEnabled;
        set
        {
            if (value == _backgroundEnabled) return;
            _backgroundEnabled = value;
            OnPropertyChanged(nameof(BackgroundEnabled));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>背景目录（留空 = 程序目录下的 Pictures）。</summary>
    public string BackgroundDirectory
    {
        get => _backgroundDirectory;
        set
        {
            if (value == _backgroundDirectory) return;
            _backgroundDirectory = value;
            OnPropertyChanged(nameof(BackgroundDirectory));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>是否允许视频背景。</summary>
    public bool BackgroundVideoEnabled
    {
        get => _backgroundVideoEnabled;
        set
        {
            if (value == _backgroundVideoEnabled) return;
            _backgroundVideoEnabled = value;
            OnPropertyChanged(nameof(BackgroundVideoEnabled));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>忙碌时是否强制播放背景。</summary>
    public bool BackgroundForcePlay
    {
        get => _backgroundForcePlay;
        set
        {
            if (value == _backgroundForcePlay) return;
            _backgroundForcePlay = value;
            OnPropertyChanged(nameof(BackgroundForcePlay));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>背景不透明度（千分比，1000 = 完全不透明）。</summary>
    public int BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0, 1000);
            if (clamped == _backgroundOpacity) return;
            _backgroundOpacity = clamped;
            OnPropertyChanged(nameof(BackgroundOpacity));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>背景模糊半径（0 = 不模糊）。</summary>
    public int BackgroundBlur
    {
        get => _backgroundBlur;
        set
        {
            var clamped = Math.Clamp(value, 0, 30);
            if (clamped == _backgroundBlur) return;
            _backgroundBlur = clamped;
            OnPropertyChanged(nameof(BackgroundBlur));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>铺放方式选项（显示名走语言资源）。</summary>
    public sealed record BackgroundSuitOption(BackgroundSuitMode Mode, string DisplayName);

    /// <summary>铺放方式选项集合（顺序与 PCL 的下拉框一致：智能/居中/适应/拉伸/平铺/四角）。</summary>
    public IReadOnlyList<BackgroundSuitOption> BackgroundSuits { get; } =
    [
        new(BackgroundSuitMode.Smart, ShellText.L("Settings.Background.Suit.Smart")),
        new(BackgroundSuitMode.Center, ShellText.L("Settings.Background.Suit.Center")),
        new(BackgroundSuitMode.Fit, ShellText.L("Settings.Background.Suit.Fit")),
        new(BackgroundSuitMode.Stretch, ShellText.L("Settings.Background.Suit.Stretch")),
        new(BackgroundSuitMode.Tile, ShellText.L("Settings.Background.Suit.Tile")),
        new(BackgroundSuitMode.TopLeft, ShellText.L("Settings.Background.Suit.TopLeft")),
        new(BackgroundSuitMode.TopRight, ShellText.L("Settings.Background.Suit.TopRight")),
        new(BackgroundSuitMode.BottomLeft, ShellText.L("Settings.Background.Suit.BottomLeft")),
        new(BackgroundSuitMode.BottomRight, ShellText.L("Settings.Background.Suit.BottomRight")),
    ];

    /// <summary>背景铺放方式。</summary>
    public BackgroundSuitMode BackgroundSuit
    {
        get => _backgroundSuit;
        set
        {
            if (value == _backgroundSuit) return;
            _backgroundSuit = value;
            OnPropertyChanged(nameof(BackgroundSuit));
            ApplyBackgroundSettings();
        }
    }

    /// <summary>背景相关设置的状态提示。</summary>
    public string BackgroundStatusMessage
    {
        get => _backgroundStatusMessage;
        private set
        {
            if (value == _backgroundStatusMessage) return;
            _backgroundStatusMessage = value;
            OnPropertyChanged(nameof(BackgroundStatusMessage));
        }
    }

    /// <summary>
    /// 把背景设置写穿到 Core 并通知界面层重新应用。
    ///
    /// 写穿到设置服务（唯一权威）+ 落盘；然后广播
    /// <see cref="AppSettings.BackgroundChanged"/>，让 MainWindow 重新铺背景。
    /// </summary>
    private void ApplyBackgroundSettings()
    {
        try
        {
            AppSettings.Settings.Set(UiSettings.BackgroundEnabledKey, _backgroundEnabled);
            AppSettings.Settings.Set(UiSettings.BackgroundDirectoryKey, _backgroundDirectory.Trim());
            AppSettings.Settings.Set(UiSettings.BackgroundVideoEnabledKey, _backgroundVideoEnabled);
            AppSettings.Settings.Set(UiSettings.BackgroundForcePlayKey, _backgroundForcePlay);
            AppSettings.Settings.Set(UiSettings.BackgroundOpacityKey, _backgroundOpacity);
            AppSettings.Settings.Set(UiSettings.BackgroundBlurKey, _backgroundBlur);
            AppSettings.Settings.Set(UiSettings.BackgroundSuitKey, _backgroundSuit);
            AppSettings.Save();
            AppSettings.RaiseBackgroundChanged();
            BackgroundStatusMessage = ShellText.L("Settings.Background.Saved");
        }
        catch (Exception ex)
        {
            BackgroundStatusMessage = ShellText.L("Settings.Background.Failed", ex.Message);
        }
    }

    // ---------------- 功能隐藏 ----------------

    private string _hiddenEntries = "";
    private bool _hideForceShow;

    /// <summary>
    /// 要隐藏的入口（逗号分隔的入口名）。
    ///
    /// 入口名取自 Core 的 <see cref="Core.UI.Customization.HideableEntry"/>：
    /// TabDownload / TabProgress / TabSettings / TabTools /
    /// ToolDownloader / ToolLogs / ToolDiagnostics / ToolLicenses /
    /// ToolFeedback / ToolMusic / ToolAbout。
    /// </summary>
    public string HiddenEntries
    {
        get => _hiddenEntries;
        set
        {
            if (value == _hiddenEntries) return;
            _hiddenEntries = value;
            OnPropertyChanged(nameof(HiddenEntries));
            ApplyHiddenEntriesSettings();
        }
    }

    /// <summary>临时显示全部入口（忽略隐藏设置）。</summary>
    public bool HideForceShow
    {
        get => _hideForceShow;
        set
        {
            if (value == _hideForceShow) return;
            _hideForceShow = value;
            OnPropertyChanged(nameof(HideForceShow));
            ApplyHiddenEntriesSettings();
        }
    }

    /// <summary>
    /// 把隐藏设置写穿到 Core 并让外壳重新算可见性。
    ///
    /// 解析不了的入口名**直接忽略**（见 <c>AppSettings.ReadHidePolicy</c>），
    /// 不给用户弹错误 —— 手改配置文件写错名字不该让界面报错。
    /// </summary>
    private void ApplyHiddenEntriesSettings()
    {
        try
        {
            AppSettings.Settings.Set(UiSettings.HideEntriesKey, _hiddenEntries.Trim());
            AppSettings.Settings.Set(UiSettings.HideForceShowKey, _hideForceShow);
            AppSettings.Save();
            AppSettings.RaiseHiddenEntriesChanged();
        }
        catch (Exception ex)
        {
            BackgroundStatusMessage = ShellText.L("Settings.Hidden.Failed", ex.Message);
        }
    }

    // ---------------- 高级材质（模糊） ----------------

    private bool _blurEnabled;
    private int _blurRadius = 16;
    private int _blurSamplingRate = 70;
    private int _blurKernelType;

    /// <summary>是否启用高级材质（界面模糊）。</summary>
    public bool BlurEnabled
    {
        get => _blurEnabled;
        set
        {
            if (value == _blurEnabled) return;
            _blurEnabled = value;
            OnPropertyChanged(nameof(BlurEnabled));
            ApplyBlurSettings();
        }
    }

    /// <summary>模糊半径。</summary>
    public int BlurRadius
    {
        get => _blurRadius;
        set
        {
            var clamped = Math.Clamp(value, 0, 60);
            if (clamped == _blurRadius) return;
            _blurRadius = clamped;
            OnPropertyChanged(nameof(BlurRadius));
            ApplyBlurSettings();
        }
    }

    /// <summary>模糊采样率（0~100）。</summary>
    public int BlurSamplingRate
    {
        get => _blurSamplingRate;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped == _blurSamplingRate) return;
            _blurSamplingRate = clamped;
            OnPropertyChanged(nameof(BlurSamplingRate));
            ApplyBlurSettings();
        }
    }

    /// <summary>模糊核类型选项。</summary>
    public sealed record BlurKernelTypeOption(int Value, string DisplayName);

    /// <summary>模糊核类型选项集合（对应 PCL 的 UiBlurType 三个取值）。</summary>
    public IReadOnlyList<BlurKernelTypeOption> BlurKernelTypes { get; } =
    [
        new(0, ShellText.L("Settings.Blur.Kernel.Gaussian")),
        new(1, ShellText.L("Settings.Blur.Kernel.Radial")),
        new(2, ShellText.L("Settings.Blur.Kernel.Diagonal")),
    ];

    /// <summary>模糊核类型选项（当前选中）。</summary>
    public BlurKernelTypeOption? SelectedBlurKernelType
    {
        get => BlurKernelTypes.FirstOrDefault(t => t.Value == _blurKernelType);
        set
        {
            if (value is null) return;
            BlurKernelType = value.Value;
        }
    }

    /// <summary>模糊核类型（0=高斯，1=径向，2=对角高斯）。</summary>
    public int BlurKernelType
    {
        get => _blurKernelType;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (clamped == _blurKernelType) return;
            _blurKernelType = clamped;
            OnPropertyChanged(nameof(BlurKernelType));
            OnPropertyChanged(nameof(SelectedBlurKernelType));
            ApplyBlurSettings();
        }
    }

    /// <summary>把模糊设置写穿到 Core 并让界面重新应用。</summary>
    private void ApplyBlurSettings()
    {
        try
        {
            AppSettings.Settings.Set(UiSettings.BlurEnabledKey, _blurEnabled);
            AppSettings.Settings.Set(UiSettings.BlurRadiusKey, _blurRadius);
            AppSettings.Settings.Set(UiSettings.BlurSamplingRateKey, _blurSamplingRate);
            AppSettings.Settings.Set(UiSettings.BlurKernelTypeKey, _blurKernelType);
            AppSettings.Save();
            AppSettings.RaiseBlurChanged();
        }
        catch (Exception ex)
        {
            BackgroundStatusMessage = ShellText.L("Settings.Blur.Failed", ex.Message);
        }
    }

    /// <summary>语言选项（首项 = 跟随系统）。</summary>
    public IReadOnlyList<ShellLocalization.LanguageOption> Languages { get; } =
        ShellLocalization.BuildOptions();

    [ObservableProperty]
    private ShellLocalization.LanguageOption? _selectedLanguage;

    /// <summary>
    /// 构造设置页 ViewModel。
    /// </summary>
    /// <param name="grayApplier">
    /// 灰度模式施加器（WPF 侧是 <c>GrayModeController</c>）。留 null 表示
    /// 「本壳没有灰度实现」—— 此时灰度开关只落设置项，不做即时视觉反馈。
    /// </param>
    public SettingsViewModel(IGrayModeApplier? grayApplier = null)
    {
        // 没显式注入时从注册表取（各壳在启动时装上自己的实现）。
        _grayApplier = grayApplier ?? ShellCapabilities.GrayModeApplier;

        var (kind, dark) = ReadCurrentTheme();
        _selectedTheme = Themes.FirstOrDefault(t => t.Kind == kind) ?? Themes[0];
        _isDark = dark;

        // 回显已保存的公告源（否则输入框是空的，用户会以为没保存过）。
        AppSettings.Load();
        _announcementSource = AppSettings.Settings.Get<string>(UiSettings.AnnouncementSourceKey);

        // 回显已保存的背景设置（否则开关/滑块会显示成默认值，与实际生效值不符）。
        _backgroundEnabled = AppSettings.Settings.Get<bool>(UiSettings.BackgroundEnabledKey);
        _backgroundDirectory = AppSettings.Settings.Get<string>(UiSettings.BackgroundDirectoryKey);
        _backgroundVideoEnabled = AppSettings.Settings.Get<bool>(UiSettings.BackgroundVideoEnabledKey);
        _backgroundForcePlay = AppSettings.Settings.Get<bool>(UiSettings.BackgroundForcePlayKey);
        _backgroundOpacity = AppSettings.Settings.Get<int>(UiSettings.BackgroundOpacityKey);
        _backgroundBlur = AppSettings.Settings.Get<int>(UiSettings.BackgroundBlurKey);
        _backgroundSuit = AppSettings.Settings.Get<BackgroundSuitMode>(UiSettings.BackgroundSuitKey);

        // 回显已保存的隐藏与模糊设置。
        _hiddenEntries = AppSettings.Settings.Get<string>(UiSettings.HideEntriesKey);
        _hideForceShow = AppSettings.Settings.Get<bool>(UiSettings.HideForceShowKey);
        _blurEnabled = AppSettings.Settings.Get<bool>(UiSettings.BlurEnabledKey);
        _blurRadius = AppSettings.Settings.Get<int>(UiSettings.BlurRadiusKey);
        _blurSamplingRate = AppSettings.Settings.Get<int>(UiSettings.BlurSamplingRateKey);
        _blurKernelType = AppSettings.Settings.Get<int>(UiSettings.BlurKernelTypeKey);

        // 回显当前生效的语言（保存值 → 解析成选项；解析不到时回落"跟随系统"）
        var savedCode = ShellLocalization.ReadSavedLanguageCode();
        _selectedLanguage = Languages.FirstOrDefault(o => o.Code == savedCode) ?? Languages[0];

        // 主题可能被别处（快捷键/其它页面）切换：跟着走，
        // 否则设置页的选中项会与实际观感脱节。
        ShellThemeController.ThemeChanged += SyncFromThemeManager;

        // 代理：回显保存的模式与地址，并把代理管理器接到网络层。
        // 优先读设置服务（跨会话保持的权威），读不到再回落到代理管理器当前状态。
        var savedProxyMode = ParseProxyMode(
            AppSettings.Settings.Get<string>(UiSettings.ProxyModeKey));
        _selectedProxyMode = ProxyModes.FirstOrDefault(m => m.Mode == savedProxyMode)
            ?? ProxyModes.FirstOrDefault(m => m.Mode == AppServices.ProxyManager.Mode)
            ?? ProxyModes[0];
        _proxyAddress = AppSettings.Settings.Get<string>(UiSettings.ProxyAddressKey);
        if (string.IsNullOrWhiteSpace(_proxyAddress))
        {
            _proxyAddress = AppServices.ProxyManager.CustomProxyAddress?.ToString() ?? "";
        }
        _proxyUsername = AppSettings.Settings.Get<string>(UiSettings.ProxyUsernameKey);
        // 密码不从界面回显（属性是只写），但**必须**读回内存副本：
        // 否则启动时 PersistProxySettings 会用空密码覆盖掉已保存的密码。
        _proxyPassword = AppSettings.Settings.Get<string>(UiSettings.ProxyPasswordKey);
        ApplyProxySettings();

        // 回显已保存的 DoH 配置（否则输入框是空的，用户会以为没保存过）。
        _dohUpstreams = AppSettings.Settings.Get<string>(UiSettings.DohUpstreamsKey);
        _dohCacheTtl = AppSettings.Settings.Get<int>(UiSettings.DohCacheTtlKey);
        _dohTimeout = AppSettings.Settings.Get<int>(UiSettings.DohTimeoutKey);

        // 窗口外观 + 下载限额：回显保存值。
        _windowOpacity = AppSettings.Settings.Get<int>(UiSettings.WindowOpacityKey);
        _lockWindowSize = AppSettings.Settings.Get<bool>(UiSettings.LockWindowSizeKey);
        _uiFont = AppSettings.Settings.Get<string>(UiSettings.FontKey);
        _titleBarText = AppSettings.Settings.Get<string>(UiSettings.TitleBarTextKey);
        _titleBarLeftAlign = AppSettings.Settings.Get<bool>(UiSettings.TitleBarLeftAlignKey);
        _downloadThreadLimit = AppSettings.Settings.Get<int>(UiSettings.DownloadThreadLimitKey);
        _downloadFileConnectionLimit = AppSettings.Settings.Get<int>(UiSettings.DownloadFileConnectionLimitKey);
        _downloadSpeedLimit = AppSettings.Settings.Get<int>(UiSettings.DownloadSpeedLimitKey);

        RefreshRecords();

        // 「行为」两项与下载页共用同一组键：下载页改了值，这里的勾选状态要跟着走，
        // 否则两个入口会各显示一半（一边是勾上、一边是没勾），看起来像设置没生效。
        AppSettings.Settings.Changed += OnSharedBehaviorKeyChanged;
    }

    /// <summary>别的入口（下载页左栏开关）改了共用键时刷新设置页的勾选状态。</summary>
    private void OnSharedBehaviorKeyChanged(string key)
    {
        if (key == UiSettings.ShowUnsupportedSourcesKey)
        {
            OnPropertyChanged(nameof(ShowUnsupportedSources));
        }
        else if (key == UiSettings.ConfirmBeforeInstallKey)
        {
            OnPropertyChanged(nameof(ConfirmBeforeInstall));
        }
    }

    /// <summary>
    /// 把代理设置应用到代理管理器，并让网络层重建客户端。
    ///
    /// <para><b>现在还会落盘</b>：以前代理只存在内存里（<see cref="_proxyAddress"/> 等字段），
    /// 关掉应用就丢；而 <see cref="Services.SetupObserver"/> 是靠设置服务的
    /// <c>Changed</c> 事件驱动"重应用"的，不落盘就既不能跨会话保持、
    /// 也不会触发观察者重跑。</para>
    /// </summary>
    private void ApplyProxySettings()
    {
        var manager = AppServices.ProxyManager;
        var mode = (SelectedProxyMode ?? ProxyModes[0]).Mode;
        manager.Mode = mode;

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

        // 落盘（供下次启动与设置观察者使用）。写相同值时设置服务会短路，不会重复通知。
        PersistProxySettings(mode);
    }

    /// <summary>把当前代理相关字段写回设置服务。</summary>
    private void PersistProxySettings(ProxyMode mode)
    {
        try
        {
            var settings = AppSettings.Settings;
            settings.Set(UiSettings.ProxyModeKey, mode switch
            {
                ProxyMode.SystemProxy => "system",
                ProxyMode.CustomProxy => "custom",
                _ => "none",
            });
            settings.Set(UiSettings.ProxyAddressKey, ProxyAddress);
            settings.Set(UiSettings.ProxyUsernameKey, ProxyUsername);
            settings.Set(UiSettings.ProxyPasswordKey, ProxyPassword);
            AppSettings.Save();
        }
        catch (Exception)
        {
            // 落盘失败不该打断界面操作：内存里的代理已经生效了。
        }
    }

    /// <summary>更新「系统代理探测状态」那一行。</summary>
    private void UpdateProxyStatus()
    {
        var manager = AppServices.ProxyManager;
        ProxyStatusMessage = manager.Mode switch
        {
            ProxyMode.NoProxy => ShellText.L("Settings.Proxy.Status.None"),
            ProxyMode.CustomProxy => ShellText.L("Settings.Proxy.Status.Custom", manager.CustomProxyAddress?.ToString() ?? "-"),
            _ => manager.SystemProxyAvailable
                ? ShellText.L("Settings.Proxy.Status.SystemFound", manager.GetProxyUrl() ?? "-")
                : ShellText.L("Settings.Proxy.Status.SystemNone"),
        };
    }

    /// <summary>切换语言（选中即应用，与主题族一致）。</summary>
    [RelayCommand]
    private void ApplyLanguage()
    {
        var option = SelectedLanguage ?? Languages[0];
        ShellLocalization.SetLanguage(option.Code);
        StatusMessage = ShellText.L("Settings.Language.Switched", option.DisplayName);
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
                ShellThemeController.ApplyFollowMode(Core.UI.Theme.ThemeFollowMode.System);
            }
            else
            {
                ShellThemeController.Apply(option.Kind, IsDark);
            }

            // 持久化：否则重启后又回到默认主题（之前审计发现的缺口）
            AppSettings.Settings.Set(UiSettings.ThemeKindKey,
                option.Kind == ThemeKind.Fluent10 ? ThemeFamily.Fluent10 : ThemeFamily.PclClassic);
            AppSettings.Settings.Set(UiSettings.DarkModeKey, IsDark);
            AppSettings.Save();

            StatusMessage = ShellText.L("Settings.Theme.Applied", option.Title, ShellText.L(IsDark ? "Settings.Dark" : "Settings.Light"));
        }
        catch (Exception ex)
        {
            // 主题字典缺失/路径写错不该让设置页崩掉：给出可读原因即可。
            // 注意 Apply 内部会触发 ThemeChanged，若它在半途失败，界面可能停在旧主题上，
            // 所以这里必须把"失败了"明确说出来，而不是假装成功。
            StatusMessage = ShellText.L("Settings.Theme.SwitchFailed", ex.Message);
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
            StatusMessage = ShellText.L("Settings.Records.Cleared");
        }
        catch (Exception ex)
        {
            StatusMessage = ShellText.L("Settings.Records.ClearFailed", ex.Message);
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
        (ShellThemeController.CurrentKind, ShellThemeController.IsDark);

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
