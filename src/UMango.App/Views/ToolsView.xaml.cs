using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using UMango.Core.Terminal;

namespace UMango.App.Views;

/// <summary>
/// 工具/诊断页：系统诊断 / 合规与许可 / 关于。
///
/// 代码后置里没有业务逻辑，只做三件事：取服务、建自己的诊断 ViewModel、Loaded 时 await 异步加载。
/// 为什么这一页的加载必须失败可见：它是"出问题时的第一站"——
/// 如果诊断过程本身出错却静默（比如探测抛异常被吞掉），用户看到的是一片空白，
/// 反而比没有这一页更糟；所以本页所有异常都会渲染成可读的中文原因。
/// </summary>
public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        ViewModel = new ToolsDiagnosticsViewModel();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = ViewModel;

        // Loaded 里 await 异步接口：构造函数不能 await，而在 UI 线程上 .Result/.Wait()
        // 等 CLI 探测会死锁（WPF 的 SynchronizationContext 被阻塞）。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>本页的 ViewModel（同一命名空间内定义，见文件末尾）。</summary>
    public ToolsDiagnosticsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 走注册表缓存（不重新起 CLI 进程）；重新探测由「重新探测」按钮显式触发。
        await ViewModel.EnsureLoadedAsync();
        SyncReportText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>
    /// 页面重新可见时，只刷新"与记录有关"的两行数字（不动包源明细、不起 CLI）。
    ///
    /// 为什么不能省：外壳用 Visibility 切页、页面只创建一次，Loaded 不会再触发；
    /// 而用户很可能是"装完一个包再回到工具页"，若不在这里补一次，
    /// 「已记录次数 / 记录文件」会停在**进入应用那一刻**的旧数字上 —— 那两行正是
    /// "次数记录"在工具页的落点，显示错的数字比不显示更糟。
    /// 走内存里的记录（不做磁盘 I/O），所以既不会卡顿，也不与「重新探测」的语义重复。
    /// </summary>
    private void ToolsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || ViewModel is null) return;   // 构造期（尚未挂 ViewModel）直接忽略

        try
        {
            ViewModel.RefreshRecordsView();
        }
        catch (Exception ex)
        {
            // 与其它 UI 事件处理器同理：异常漏出去会终止进程，这里退化成一行可读提示。
            ViewModel.StatusMessage = $"刷新记录显示失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 诊断报告是多行原文（含动态生成的对照表），刻意**不用绑定**而在这里赋值。
    /// 为什么：报告正文由 ViewModel 逐字符计算，绑定只会在每次属性变更时重算一遍；
    /// 而报告只在"加载完成/重新探测完成"这两个时刻需要刷新，显式同步更省也更直观。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolsDiagnosticsViewModel.DiagnosticsText)) SyncReportText();
    }

    private void SyncReportText() => DiagnosticsReportBox.Text = ViewModel.DiagnosticsText;
}

/// <summary>
/// 工具页 ViewModel：系统判断 + 逐包源 CLI 探测明细 + 合规声明/第三方许可。
///
/// 与既有 <see cref="UMango.App.ViewModels.ToolsViewModel"/> 的分工：
///   · 既有那份覆盖"平台/记录/终端通道 + 包源可用性概览"（由 ViewModels 目录的文件负责维护）；
///   · 本类是**本页面自己的**诊断视图：真正把每个包源的 CLI 路径、CLI 版本、探测明细
///     一行一行列出来（需求对工具页的要求），并承载合规声明与第三方许可清单。
///   刻意取一个不同的类名，避免与 ViewModels 目录里的同名类型在同一次引用里产生歧义。
/// </summary>
public partial class ToolsDiagnosticsViewModel : ObservableObject
{
    /// <summary>CLI 探测明细行（逐包源）。</summary>
    public ObservableCollection<DiagnosticSourceRow> SourceRows { get; } = [];

    /// <summary>第三方组件与许可清单（合规要求：界面内必须能看到）。</summary>
    public IReadOnlyList<ThirdPartyComponent> ThirdPartyComponents { get; } =
    [
        new("CommunityToolkit.Mvvm", "MIT",
            "MVVM 基础设施（ObservableObject / RelayCommand 与源生成器）",
            "https://github.com/CommunityToolkit/dotnet"),
        new("lucide", "ISC",
            "界面全部矢量图标（PCL-CE 亦使用同一图标集）",
            "https://lucide.dev/"),
        new("simple-icons", "CC0 1.0",
            "npm / pnpm / git / Homebrew 品牌图标（winget 与 scoop 用自绘字母徽章）",
            "https://github.com/simple-icons/simple-icons")
    ];

    /// <summary>平台（Windows / macOS / Linux）。</summary>
    public string PlatformText { get; } = SystemInfo.Platform.DisplayName();

    /// <summary>平台 + 架构摘要，例如 "macOS arm64"。</summary>
    public string PlatformSummaryText { get; } = SystemInfo.Describe();

    /// <summary>架构（x64 / arm64）。</summary>
    public string ArchitectureText { get; } = SystemInfo.Architecture;

    /// <summary>是否 Windows（包源平台门控的依据之一，界面上明确写出来）。</summary>
    public bool IsWindows { get; } = SystemInfo.IsWindows;

    /// <summary>用户主目录（各包管理器的安装目录通常在其下，排查路径问题时用得上）。</summary>
    public string HomeDirectoryText { get; } = SystemInfo.HomeDirectory;

    /// <summary>本次进程的宿主描述（真机 Windows vs CrossOver/Wine 下运行，结论不同）。</summary>
    public string HostText { get; } = BuildHostText();

    /// <summary>版本号（取程序集版本，例如 0.1.0）。</summary>
    public string VersionText { get; } = BuildVersionText();

    /// <summary>构建信息：运行时版本 / 操作系统描述 / 进程架构。</summary>
    public string BuildInfoText { get; } = BuildBuildInfoText();

    /// <summary>程序所在路径（诊断时用来确认跑的是哪一份产物）。</summary>
    public string AppPathText { get; } = BuildAppPathText();

    /// <summary>管理记录文件路径。</summary>
    public string RecordsPathText => AppServices.Records.StorePath;

    /// <summary>已记录的操作次数摘要。</summary>
    public string RecordsCountText =>
        $"共 {AppServices.Records.TotalCount} 次（成功 {AppServices.Records.SuccessCount} / "
        + $"失败 {AppServices.Records.FailureCount}）";

    /// <summary>本次诊断的时间戳（"探测结果是什么时候的"必须写在界面上）。</summary>
    [ObservableProperty]
    private string _probeTimeText = "";

    /// <summary>中性提示（加载完成、重新探测完成等）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>面向用户的一句话错误。诊断页更需要它：探测失败本身就是最重要的诊断结论。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>探测明细里"未在真机验证"的集中提示（有则显示，没有则整块隐藏）。</summary>
    [ObservableProperty]
    private string _verificationText = "";

    [ObservableProperty]
    private bool _hasVerificationNotes;

    /// <summary>可直接阅读的多行系统诊断报告（平台/记录/终端通道 + 逐包源明细）。</summary>
    [ObservableProperty]
    private string _diagnosticsText = "";

    /// <summary>初始化阶段被兜底吞掉的错误（正常为空）。为空时界面隐藏该行。</summary>
    [ObservableProperty]
    private string _initializationWarningText = "";

    [ObservableProperty]
    private bool _hasInitializationWarning;

    private bool _initialized;
    private bool _loading;

    public ToolsDiagnosticsViewModel()
    {
        RefreshDiagnosticsText();   // 先给一份不含探测结果的文本，避免首帧空白（探测要起 CLI，可能要几秒）

        // 故意不 await：构造函数不能阻塞 UI 线程，加载失败也已在本类内部处理。
        _ = EnsureLoadedAsync();
    }

    /// <summary>首次进入工具页时调用（幂等）；页面在 Loaded 里 await 它。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_initialized) return;
        _initialized = await LoadRowsAsync();
    }

    /// <summary>重新探测全部包源（用户刚装了 CLI，或怀疑缓存过期时点它）。</summary>
    /// <remarks>
    /// 必须显式传 <paramref name="refresh"/> = true：注册表默认返回**缓存**的可用性，
    /// 不写这个参数按钮就只是"用缓存重画一遍"，而按钮与提示都写着"重新执行 CLI 探测"。
    /// </remarks>
    [RelayCommand]
    private async Task Repro() => await LoadRowsAsync(refresh: true);

    /// <summary>
    /// 只刷新由「管理记录」派生出来的显示（记录文件路径、已记录次数）与诊断报告文本。
    /// 记录是内存里的快照，重算极廉价；包源明细不在这条路径上，因此不会起任何进程。
    /// </summary>
    public void RefreshRecordsView()
    {
        OnPropertyChanged(nameof(RecordsPathText));
        OnPropertyChanged(nameof(RecordsCountText));
        RefreshDiagnosticsText();   // 报告正文里也含记录数字，一并重算（文本框由 PropertyChanged 同步）
    }

    private async Task<bool> LoadRowsAsync(bool refresh = false)
    {
        if (_loading) return false;
        _loading = true;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            // 一次调用拿全分类（标题/安装目录/平台门控），再一次拿每个包源的探测结果（CLI 路径/版本/明细）。
            // 注册表内部有缓存，refresh=false 时不会重复起 CLI 进程。
            var categories = await AppServices.Registry.GetCategoriesAsync(refresh);

            SourceRows.Clear();
            foreach (var manager in AppServices.Registry.All)
            {
                var category = categories.FirstOrDefault(c => c.Kind == manager.Kind);
                var availability = await AppServices.Registry.GetAvailabilityAsync(manager.Kind, refresh);
                SourceRows.Add(new DiagnosticSourceRow(manager, category, availability));
            }

            var notes = SourceRows
                .Where(r => r.HasVerificationNote)
                .Select(r => $"· {r.Title}：{r.VerificationNote}")
                .ToList();
            VerificationText = notes.Count == 0
                ? ""
                : "以下包源的解析逻辑尚未在真机验证，显示结果仅供参考：\n" + string.Join("\n", notes);
            HasVerificationNotes = notes.Count > 0;

            ProbeTimeText = $"探测时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
            RefreshDiagnosticsText();

            // 记录数字也随这次加载一起刷新：RecordsPathText / RecordsCountText 是计算属性，
            // 不会自己收到变更通知（见 RefreshRecordsView 的说明）。
            OnPropertyChanged(nameof(RecordsPathText));
            OnPropertyChanged(nameof(RecordsCountText));

            HasInitializationWarning = AppServices.LastError is not null;
            InitializationWarningText = AppServices.LastError ?? "";

            StatusMessage = refresh
                ? $"已重新探测 {SourceRows.Count} 个包源。"
                : $"已加载 {SourceRows.Count} 个包源的探测结果。";
            return true;
        }
        catch (Exception ex)
        {
            // 诊断失败本身也是结论：把原因显示出来，并保留上一份报告（不把页面清空）。
            ErrorMessage = $"包源探测失败：{ex.Message}";
            return false;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 重算诊断报告文本。
    /// 之所以只在"探测完成"这类时点显式调用、而不是做成绑定属性：
    /// 报告需要遍历全部行并拼字符串，若放进属性 getter，绑定会在每次变更通知时重算一遍。
    /// </summary>
    private void RefreshDiagnosticsText() => DiagnosticsText = BuildDiagnosticsText();

    /// <summary>
    /// 拼装纯文本诊断报告。
    /// 为什么除了界面表格还要一段文本：用户反馈问题时会复制粘贴，
    /// 文本形态可以直接贴进 issue，而不必逐行截图。
    /// </summary>
    private string BuildDiagnosticsText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{VersionText}｜{PlatformSummaryText}｜{HostText}");
        sb.AppendLine($"主目录：{HomeDirectoryText}");
        sb.AppendLine($"记录文件：{RecordsPathText}");
        sb.AppendLine($"记录条数：{RecordsCountText}");
        sb.AppendLine($"调试输出：{(TerminalBridge.DebugMode ? "开启" : "关闭")}");
        if (AppServices.LastError is { } initError) sb.AppendLine($"初始化警告：{initError}");
        sb.AppendLine();
        sb.AppendLine("包源明细：");

        if (SourceRows.Count == 0)
        {
            sb.AppendLine("（尚未探测，点「重新探测」获取）");
            return sb.ToString().TrimEnd();
        }

        foreach (var row in SourceRows)
        {
            sb.AppendLine($"· {row.Title}｜{row.StateText}｜当前平台支持：{(row.SupportedHere ? "是" : "否")}");
            sb.AppendLine($"    CLI：{row.CliPathText}｜版本：{row.CliVersionText}");
            sb.AppendLine($"    安装目录：{row.InstallDirectory}");
            if (row.HasDetail) sb.AppendLine($"    明细：{row.Detail}");
            if (row.HasVerificationNote) sb.AppendLine($"    未在真机验证：{row.VerificationNote}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHostText()
    {
        // macOS/Linux 上运行的是 CrossOver/Wine 托管的 Windows 目标，
        // 探测结论（尤其是路径与 CLI 发现）与真机 Windows 不同，必须写清楚。
        if (SystemInfo.IsWindows)
            return OperatingSystem.IsWindows() ? "Windows 原生" : "Wine 兼容层（Windows 目标）";
        return $"非 Windows 宿主（{RuntimeInformation.RuntimeIdentifier}）";
    }

    private static string BuildVersionText()
    {
        // Application.ResourceAssembly 在无 WPF 宿主（无头测试/设计器）时可能抛，
        // 故用程序集名兜底，并且真取不到版本时也不显示 "0.0.0.0.0"。
        try
        {
            var assembly = Application.ResourceAssembly ?? Assembly.GetEntryAssembly();
            var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return $"版本 {informational.Split('+')[0]}";   // 去掉 SourceLink 附加的 +commit 后缀

            var version = assembly?.GetName().Version?.ToString(3);
            return string.IsNullOrWhiteSpace(version) ? "UMango" : $"版本 {version}";
        }
        catch (Exception)
        {
            return "UMango";
        }
    }

    private static string BuildBuildInfoText()
    {
        var framework = Application.ResourceAssembly is { } assembly
            ? assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName
            : null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(framework)) parts.Add($"目标框架：{framework}");
        parts.Add($"运行时：{RuntimeInformation.FrameworkDescription}");
        parts.Add($"操作系统：{RuntimeInformation.OSDescription}");
        parts.Add($"进程架构：{RuntimeInformation.ProcessArchitecture}");

        try
        {
            var location = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(location)) parts.Add($"可执行文件：{location}");
        }
        catch (Exception)
        {
            // 某些受限环境读不到 MainModule（权限/沙箱）：这只是诊断的补充行，跳过即可。
        }

        return string.Join("\n", parts);
    }

    private static string BuildAppPathText()
    {
        try
        {
            return AppContext.BaseDirectory;
        }
        catch (Exception)
        {
            return "（不可用）";
        }
    }
}

/// <summary>
/// 工具页「系统诊断」里的一行：包管理器名 / 安装目录 / CLI 路径 / CLI 版本 / 状态 / 明细。
/// 由 <see cref="IPackageManager"/> 与 <see cref="SourceAvailability"/> 组合而成——
/// CLI 路径与版本只在探测结果里，安装目录只在适配器上，界面要一行显示就得在这里合并。
/// </summary>
public sealed class DiagnosticSourceRow
{
    internal DiagnosticSourceRow(
        IPackageManager manager, PackageSourceCategory? category, SourceAvailability availability)
    {
        Kind = manager.Kind;
        Title = manager.DisplayName;
        InstallDirectory = manager.InstallDirectory;
        CliName = manager.CliName;

        var categoryState = category is { } c ? c.AvailabilityState : availability.State;
        State = categoryState;
        StateText = StateToText(categoryState);
        StateBrush = StateToBrush(categoryState);
        // 字形与状态语义一致：● 可用 / ○ 需要处理（未装 CLI、不可达）/ ▬ 平台不支持。
        StatusGlyph = categoryState switch
        {
            AvailabilityState.Available or AvailabilityState.Degraded => "●",
            AvailabilityState.UnsupportedPlatform => "▬",
            _ => "○"
        };
        SupportedHere = category?.SupportedHere ?? manager.Kind.Supports(SystemInfo.Platform);

        // CLI 路径/版本探测不到时给出**可读**的占位，而不是空白单元格：
        // 空白既可能是"没探到"也可能是"界面坏了"，写清楚才不产生歧义。
        CliPathText = string.IsNullOrWhiteSpace(availability.CliPath) ? "（未找到）" : availability.CliPath!;
        CliVersionText = string.IsNullOrWhiteSpace(availability.CliVersion) ? "（未知）" : availability.CliVersion!;

        Detail = availability.Detail ?? category?.Detail;
        VerificationNote = availability.VerificationNote ?? category?.VerificationNote ?? manager.VerificationNote;
        CheckedAtText = availability.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        InstallHintText = string.IsNullOrWhiteSpace(category?.InstallHint)
            ? manager.InstallHint
            : category!.InstallHint;

        // 只在"装了 CLI 就能用"的情况下给安装指引：
        // 平台不支持的包源装不了、可用的包源不需要，两种情况都显示指引只会干扰阅读。
        ShowInstallHint = categoryState == AvailabilityState.NotInstalled
                          && !string.IsNullOrWhiteSpace(InstallHintText);
    }

    public PackageSourceKind Kind { get; }
    public string Title { get; }
    public string InstallDirectory { get; }
    public string CliName { get; }
    public AvailabilityState State { get; }
    public string StateText { get; }
    public string StatusGlyph { get; }
    public Brush StateBrush { get; }
    public bool SupportedHere { get; }
    public string CliPathText { get; }
    public string CliVersionText { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public string? VerificationNote { get; }
    public bool HasVerificationNote => !string.IsNullOrWhiteSpace(VerificationNote);
    public string CheckedAtText { get; }
    public string InstallHintText { get; }
    public bool ShowInstallHint { get; }

    private static string StateToText(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => "可用",
        AvailabilityState.Degraded => "可用（降级）",
        AvailabilityState.UnsupportedPlatform => "当前平台不支持",
        AvailabilityState.NotInstalled => "未安装命令行工具",
        _ => "不可达"
    };

    private static Brush StateToBrush(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => ThemeBrushes.Get("BrC3", "#1370F3"),
        AvailabilityState.Degraded => ThemeBrushes.Get("BrC6", "#D5E6FD"),
        AvailabilityState.NotInstalled or AvailabilityState.Unreachable => ThemeBrushes.Get("BrRed", "#FF4C4C"),
        _ => ThemeBrushes.Get("BrG3", "#8C8C8C")
    };
}

/// <summary>第三方组件清单的一行（名称 / 许可 / 用途 / 主页）。</summary>
public sealed class ThirdPartyComponent
{
    internal ThirdPartyComponent(string name, string license, string purpose, string url)
    {
        Name = name;
        License = license;
        Purpose = purpose;
        Url = url;
        LicenseText = $"许可：{license}";
    }

    public string Name { get; }
    public string License { get; }
    public string LicenseText { get; }
    public string Purpose { get; }
    public string Url { get; }
}
