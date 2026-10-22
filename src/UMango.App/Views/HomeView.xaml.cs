using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using UMango.Core.Records;

namespace UMango.App.Views;

/// <summary>
/// 主页（概览）：应用信息 / 包源状态 / 使用统计（次数记录）/ 最近操作。
///
/// 为什么代码后置里几乎没有逻辑：
///   外壳（MainWindow）只会 <c>new HomeView()</c>，页面自己做三件事即可——
///   取服务、建自己的 ViewModel、在 Loaded 时 await 异步加载。真正的取数逻辑都在
///   <see cref="HomeViewModel"/> 里，页面只负责绑定，这样以后换掉界面不用动业务代码。
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        ViewModel = new HomeViewModel();
        DataContext = ViewModel;

        // 语言切换 → 重建代码侧拼的本地化句子（XAML 的 DynamicResource 会自动更新，不用管）
        Unloaded += (_, _) => Localization.AppLocalization.LocalizedTextRefreshRequested -= ViewModel.RefreshLocalizedText;
        Localization.AppLocalization.LocalizedTextRefreshRequested += ViewModel.RefreshLocalizedText;

        // 用 Loaded 而不是构造函数：构造函数里 await 不了（会阻塞 UI 线程），
        // 而 .Result/.Wait() 在 UI 线程上等 CLI 探测会直接死锁。
        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel（同一命名空间内定义，见文件末尾）。</summary>
    public HomeViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次加载走注册表缓存（不重新起 CLI 进程）；刷新由「刷新」按钮显式触发。
        await ViewModel.EnsureLoadedAsync();
    }
}

/// <summary>
/// 主页 ViewModel。
///
/// 为什么放在 Views 命名空间而不是 ViewModels：
///   主页的数据全部来自 Core 的现成 API（注册表 + 记录存储），没有额外业务逻辑，
///   把它和页面放在一起可以让"页面 + 它的取数"成为一个自洽单元，
///   也避免与负责 ViewModels 目录的文件产生归属冲突。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>最近操作卡片只列最近 10 条：这是概览页，看全量去设置页的记录管理。</summary>
    private const int RecentLimit = 10;

    /// <summary>
    /// 概览页**永远**展示全部 6 个包源（含当前平台不支持的）。
    /// 与下载页"默认只显示可用的"是相反的取舍：下载页关心"能装什么"，
    /// 主页关心"我有哪些包源、它们现在什么状态"。
    /// </summary>
    private static readonly PackageSourceKind[] SourceOrder =
    [
        PackageSourceKind.Npm,
        PackageSourceKind.Pnpm,
        PackageSourceKind.Git,
        PackageSourceKind.Scoop,
        PackageSourceKind.Winget,
        PackageSourceKind.Homebrew
    ];

    private bool _initialized;
    private bool _loading;

    /// <summary>包源状态行（标题=包管理器名、副标题=安装目录，与下载页左栏口径一致）。</summary>
    public ObservableCollection<HomeSourceStatus> Sources { get; } = [];

    /// <summary>「按包源次数」小列表（只列有记录的包源）。</summary>
    public ObservableCollection<HomeSourceCount> SourceCounts { get; } = [];

    /// <summary>最近操作（最多 10 条，时间倒序）。</summary>
    public ObservableCollection<HomeRecordRow> RecentRecords { get; } = [];

    /// <summary>系统判断摘要，例如 "macOS arm64"（Core 的 SystemInfo.Describe）。</summary>
    public string PlatformText { get; } = SystemInfo.Describe();

    /// <summary>架构（x64 / arm64）。</summary>
    public string ArchitectureText { get; } = SystemInfo.Architecture;

    /// <summary>
    /// 是否运行在 Windows 上。
    /// 写成布尔属性（而不是在 XAML 里比较字符串）是因为界面只需要"是/否"，
    /// 而 Describe() 的文案将来可能调整，界面不该跟着改。
    /// </summary>
    public bool IsWindows { get; } = SystemInfo.IsWindows;

    /// <summary>
    /// "运行在 Windows："/"Running on Windows:" 这类带参数的句子。
    ///
    /// 为什么在 ViewModel 里拼而不用 XAML 的 <c>{DynamicResource Home.RunningOn}</c> 直接取：
    /// <c>DynamicResource</c> 只做查表、**不做 string.Format**，占位符 <c>{0}</c> 会原样显示。
    /// 带参数的本地化句子必须经代码侧格式化（对应 PCL 的 <c>Lang.Text(key, args)</c>）。
    /// 语言切换时由 <see cref="RefreshLocalizedText"/> 重建本属性。
    /// </summary>
    public string RunningOnText =>
        string.Format(Localization.AppLocalization.Text("Home.RunningOn"),
            Localization.AppLocalization.Text("Home.Platform.Windows"));

    /// <summary>语言切换后重建本地化文本属性。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(RunningOnText));
    }

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>面向用户的一句话错误（中文）。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;
    /// <summary>总次数（管理记录总条数）。</summary>
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _failureCount;

    /// <summary>成功率文字，例如 "成功率 75.0%"；没有记录时给"暂无记录"。</summary>
    [ObservableProperty]
    private string _successRateText = "暂无记录";

    /// <summary>次数统计的补充说明（记录文件路径 + 按包源条数）。</summary>
    [ObservableProperty]
    private string _recordsDetailText = "";

    /// <summary>最近操作区的提示；没有记录时用它替代空列表（空列表会让人以为加载失败）。</summary>
    [ObservableProperty]
    private string _recentHintText = "";

    /// <summary>
    /// 探测明细的扁平文本（只含有内容的包源）。见 <see cref="HasSourceDetails"/> 的说明：
    /// 做成整段文本而不是每源一行，是为了让"正常可用且无话可说"的包源不产生空行。
    /// </summary>
    [ObservableProperty]
    private string _sourceDetailText = "";

    /// <summary>是否有包源带探测明细（无则整段折叠）。</summary>
    [ObservableProperty]
    private bool _hasSourceDetails;

    /// <summary>
    /// 「未在真机验证」提示的扁平文本（Core 的 VerificationNote）。
    /// 单独成段的原因：这是合规/诚信声明，不能混在普通诊断明细里被读者的视线跳过。
    /// </summary>
    [ObservableProperty]
    private string _verificationText = "";

    [ObservableProperty]
    private bool _hasVerificationNotes;

    /// <summary>首次进入主页时调用（幂等），由 Loaded 事件 await。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_initialized) return;
        _initialized = await RefreshAsync(skipRecordsReload: true);
    }

    /// <summary>
    /// 刷新全部数据（包源强制重新探测 + 重新读记录文件）。
    /// 用户装了新 CLI、或在别的窗口装完包之后点它。
    /// </summary>
    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshAsync(skipRecordsReload: false);
    }

    private async Task<bool> RefreshAsync(bool skipRecordsReload)
    {
        if (_loading) return false;
        _loading = true;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            // 记录侧是纯内存 + 文件读取，很快；包源侧会起 CLI 进程，必须 await 异步接口。
            RefreshRecords(skipRecordsReload);

            var categories = await AppServices.Registry.GetCategoriesAsync(refresh: !skipRecordsReload);
            RefreshSources(categories);
            RefreshSourceCounts();

            StatusMessage = $"已更新：{Sources.Count} 个包源，{TotalCount} 条记录。";
            return true;
        }
        catch (Exception ex)
        {
            // 概览页不能因为"取数失败"变空白：显示原因，保留上一份数据。
            ErrorMessage = $"概览数据加载失败：{ex.Message}";
            return false;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    private void RefreshSources(IReadOnlyList<PackageSourceCategory> categories)
    {
        Sources.Clear();

        // 以 Core 返回的分类为准，但按固定顺序输出：
        // 界面每次刷新后行的顺序都一样，用户才不会觉得"列表在乱跳"。
        foreach (var kind in SourceOrder)
        {
            var category = categories.FirstOrDefault(c => c.Kind == kind);
            Sources.Add(category is not null
                ? new HomeSourceStatus(category)
                : new HomeSourceStatus(kind));   // 注册表缺该适配器时也要有一行（如实显示"未注册"）
        }

        RefreshSourceDetails();
    }

    /// <summary>
    /// 把"有明细的包源"和"未在真机验证的包源"各自拍平成一整段多行文本。
    ///
    /// 为什么不在 XAML 里逐行渲染：ItemsControl 无法按条件过滤元素，
    /// 若直接绑 Sources，六个包源里没明细的那些会渲染成空行（列表被拉长却看不到信息）；
    /// 而"未验证"单独成段的目的是把它当声明读，而不是混在诊断明细里。
    /// </summary>
    private void RefreshSourceDetails()
    {
        var details = Sources
            .Where(s => s.HasDetail)
            .Select(s => $"· {s.Title} · {s.DetailText}")
            .ToList();
        SourceDetailText = string.Join("\n", details);
        HasSourceDetails = details.Count > 0;

        var notes = Sources
            .Where(s => s.HasVerificationNote)
            .Select(s => $"· {s.Title}：{s.VerificationNote}")
            .ToList();
        VerificationText = notes.Count == 0
            ? ""
            : "以下包源的解析逻辑尚未在真机验证，结果仅供参考：\n" + string.Join("\n", notes);
        HasVerificationNotes = notes.Count > 0;
    }

    private void RefreshRecords(bool skipReload)
    {
        var store = AppServices.Records;
        if (!skipReload) store.Load();   // Load 内部已容错（文件损坏/无权限时保持原样）

        var records = store.Records;     // 已是时间倒序快照；不要在这里再排一次序

        TotalCount = store.TotalCount;
        SuccessCount = store.SuccessCount;
        FailureCount = store.FailureCount;
        SuccessRateText = TotalCount == 0
            ? "暂无记录"
            : $"成功率 {SuccessCount * 100.0 / TotalCount:0.0}%（失败 {FailureCount} 次）";

        RecentRecords.Clear();
        foreach (var record in records.Take(RecentLimit))
            RecentRecords.Add(new HomeRecordRow(record));

        RecentHintText = TotalCount > 0
            ? $"共 {TotalCount} 条记录，这里显示最近 {RecentRecords.Count} 条。"
            : "还没有任何管理记录；在「下载」页安装一个包之后，这里会列出每次操作的成败与耗时。";
    }

    private void RefreshSourceCounts()
    {
        SourceCounts.Clear();

        // 固定按 SourceOrder 逐源取次数（而不是遍历 CountBySource 字典）：
        // 顺序稳定、且 0 次的包源也能显示出来（"这个源我还没用过"本身也是信息）。
        foreach (var kind in SourceOrder)
        {
            var count = AppServices.Records.CountOf(kind);
            SourceCounts.Add(new HomeSourceCount(SourceName(kind), count, TotalCount));
        }

        RecordsDetailText = $"记录文件：{AppServices.Records.StorePath}";
    }

    private static string SourceName(PackageSourceKind kind) =>
        AppServices.Registry.Find(kind)?.DisplayName ?? kind.DisplayName();
}

/// <summary>
/// 主页「包源状态」卡的一行：包管理器名 / 安装目录 / 状态文字 / 状态点颜色 / 未验证提示。
/// 包装（而不是直接绑 <see cref="PackageSourceCategory"/>）的原因：
/// 状态文字与状态点颜色是**界面的表达**，让 Core 类型保持纯数据，改动界面不会波及业务层。
/// </summary>
public sealed class HomeSourceStatus
{
    internal HomeSourceStatus(PackageSourceCategory category)
        : this(category.Kind, category.Title, category.Subtitle, category.AvailabilityState,
               category.Detail, category.VerificationNote)
    {
    }

    /// <summary>注册表里没有该包源适配器时的兜底行（如实显示原因，而不是让包源凭空消失）。</summary>
    internal HomeSourceStatus(PackageSourceKind kind)
        : this(kind, kind.DisplayName(), "（未注册该包源适配器）", AvailabilityState.Unreachable,
               detail: "包源注册表中没有该适配器", verificationNote: null)
    {
    }

    private HomeSourceStatus(
        PackageSourceKind kind, string title, string subtitle, AvailabilityState state,
        string? detail, string? verificationNote)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        State = state;
        StateText = StateToText(state);
        StatusBrush = StatusToBrush(state);
        DetailText = detail ?? "";
        HasDetail = !string.IsNullOrWhiteSpace(detail);
        VerificationNote = verificationNote;
        HasVerificationNote = !string.IsNullOrWhiteSpace(verificationNote);

        // 形状与状态语义一致：● 可用 / ○ 需要处理（未装 CLI、不可达）/ ▬ 平台不支持。
        // 色点 + 文字双重表达，色盲用户与深色主题下同样能读出来。
        StatusGlyph = state switch
        {
            AvailabilityState.Available or AvailabilityState.Degraded => "●",
            AvailabilityState.UnsupportedPlatform => "▬",
            _ => "○"
        };
    }

    public PackageSourceKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public AvailabilityState State { get; }

    /// <summary>状态文字（中文；界面不显示枚举名，用户看不懂）。</summary>
    public string StateText { get; }

    /// <summary>状态点字形（●可用 / ○不可用 / ▬平台不支持）。</summary>
    public string StatusGlyph { get; }

    /// <summary>
    /// 状态点颜色。**在 C# 里解析主题 token 并按值传出去**，而不是在 XAML 里写死颜色：
    /// 硬编码色值会让深色主题下"绿点糊在深色底上"，而按 key 取则随主题字典替换自动更新。
    /// </summary>
    public Brush StatusBrush { get; }

    /// <summary>探测到的明细原因（CLI 缺失、网络不可达等）。</summary>
    public string DetailText { get; }
    public bool HasDetail { get; }

    /// <summary>「未在真机验证」的如实说明（来自适配器的 VerificationNote）；null = 已验证。</summary>
    public string? VerificationNote { get; }
    public bool HasVerificationNote { get; }

    private static string StateToText(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => "可用",
        AvailabilityState.Degraded => "可用（降级）",
        AvailabilityState.UnsupportedPlatform => "当前平台不支持",
        AvailabilityState.NotInstalled => "未安装命令行工具",
        _ => "不可达"
    };

    private static Brush StatusToBrush(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => ThemeBrushes.Get("BrC3", "#1370F3"),        // 蓝 = 正常
        AvailabilityState.Degraded => ThemeBrushes.Get("BrC6", "#D5E6FD"),         // 浅蓝 = 降级但仍可用
        AvailabilityState.NotInstalled or AvailabilityState.Unreachable
            => ThemeBrushes.Get("BrRed", "#FF4C4C"),                               // 红 = 需要处理
        _ => ThemeBrushes.Get("BrG3", "#8C8C8C")                                   // 灰 = 平台门控，不是错误
    };
}

/// <summary>主页「使用统计」卡里的一行：包源名 / 次数 / 占比文字。</summary>
public sealed class HomeSourceCount
{
    internal HomeSourceCount(string title, int count, int total)
    {
        Title = title;
        Count = count;
        ShareText = total <= 0 ? "—" : $"{count * 100.0 / total:0.#}%";
    }

    public string Title { get; }
    public int Count { get; }

    /// <summary>占总次数的比例文字（总量为 0 时给 "—" 而不是 0.0%）。</summary>
    public string ShareText { get; }
}

/// <summary>主页「最近操作」卡里的一行：包名 / 操作 / 时间 / 成功与否 / 耗时。</summary>
public sealed class HomeRecordRow
{
    internal HomeRecordRow(InstallRecord record)
    {
        PackageName = record.PackageName;
        VersionText = string.IsNullOrWhiteSpace(record.Version) ? "" : record.Version!;
        ActionText = ActionToText(record.Action);
        TimeText = record.OccurredAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        StateText = record.Success ? "成功" : "失败";
        StateBrush = record.Success
            // 成功/失败色点与包源状态点共用同一套语义色，读起来才一致
            ? ThemeBrushes.Get("BrC3", "#1370F3")
            : ThemeBrushes.Get("BrRed", "#FF4C4C");
        StateGlyph = record.Success ? "●" : "✕";

        DetailText = BuildDetail(record);
    }

    public string PackageName { get; }
    public string VersionText { get; }
    public string ActionText { get; }
    public string TimeText { get; }
    public string StateText { get; }
    public string StateGlyph { get; }
    public Brush StateBrush { get; }
    public string DetailText { get; }

    /// <summary>
    /// 拼装可悬浮查看的明细：一行列表塞不下这么多内容，
    /// 但又不能丢掉（错误码与安装目录正是排查时最需要的东西）。
    /// </summary>
    private static string BuildDetail(InstallRecord record)
    {
        var parts = new List<string>
        {
            $"{record.Source.DisplayName()}｜{ActionToText(record.Action)}",
            $"时间：{record.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        };

        if (!string.IsNullOrWhiteSpace(record.Version)) parts.Add($"版本：{record.Version}");
        if (!string.IsNullOrWhiteSpace(record.InstallDirectory)) parts.Add($"安装目录：{record.InstallDirectory}");
        parts.Add($"耗时：{record.DurationMs} 毫秒");
        if (!string.IsNullOrWhiteSpace(record.ErrorCode)) parts.Add($"错误码：{record.ErrorCode}");
        if (!string.IsNullOrWhiteSpace(record.Detail)) parts.Add($"明细：{record.Detail}");

        return string.Join("\n", parts);
    }

    /// <summary>操作类型 → 中文（记录里存的是 install / uninstall / upgrade 这类稳定标识）。</summary>
    private static string ActionToText(string action) => action switch
    {
        "install" => "安装",
        "uninstall" => "卸载",
        "upgrade" => "升级",
        _ => action
    };
}

/// <summary>
/// 按主题 key 取画刷的小工具。**由本页与工具页共用**（两个页面的状态点/成败点都走它）。
/// 存在的理由：颜色必须在运行时随主题字典替换，而"写死色值"的绑定不会跟着变；
/// C# 侧需要画刷时统一走这里，避免每个类各写一份 TryFindResource + 兜底逻辑。
/// </summary>
internal static class ThemeBrushes
{
    public static Brush Get(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch (Exception)
        {
            // 无 WPF 应用上下文（无头测试/设计器）时 Application.Current 可能抛：
            // 这里在绑定求值路径上，抛出去会让整行渲染失败，退回落色更安全。
        }

        var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        solid.Freeze();
        return solid;
    }
}
