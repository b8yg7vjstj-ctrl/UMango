// SPDX-License-Identifier: Apache-2.0
//
// 工具页「系统诊断」视图模型（平台中立）—— 从 WPF 壳下沉到 Shell。
//
// 下沉原因（需求里的「降低重复代码率」）：诊断页是排查问题的入口，
// WPF 壳与 Avalonia 壳必须给出**完全一致**的结论与措辞；
// 若两边各写一份，很快就会漂移（一边加了「未验证」标注、另一边没加）。
//
// 与 WPF 侧原实现的有意差异：
//   1. **状态颜色用主题 key 而不是画刷**。WPF 侧返回 `Brush`（依赖 System.Windows.Media），
//      Avalonia 侧无法复用；这里改成返回 `StateBrushKey`（如 "BrC3"），
//      由各壳用各自的资源查找解析成画刷 —— 颜色仍随主题变化。
//   2. 第三方组件清单的「用途」文案在两壳里相同，直接引用同一份语言键。

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.Localization;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using UMango.Core.Terminal;
using UMango.Shell.Services;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 工具页诊断视图：系统判断 + 逐包源 CLI 探测明细 + 合规声明/第三方许可。
///
/// <para>
/// 与 <see cref="ToolsViewModel"/> 的分工：
///   · 那一份覆盖「平台/记录/终端通道 + 包源可用性概览」；
///   · 本类是**本页面自己的**诊断视图：把每个包源的 CLI 路径、CLI 版本、探测明细
///     一行一行列出来（需求对工具页的要求），并承载合规声明与第三方许可清单。
/// </para>
/// </summary>
public partial class ToolsDiagnosticsViewModel : ObservableObject
{
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    /// <summary>CLI 探测明细行（逐包源）。</summary>
    public ObservableCollection<DiagnosticSourceRow> SourceRows { get; } = [];

    /// <summary>第三方组件与许可清单（合规要求：界面内必须能看到）。</summary>
    public IReadOnlyList<ThirdPartyComponent> ThirdPartyComponents { get; } =
    [
        new("CommunityToolkit.Mvvm", "MIT",
            L("Tools.Dependency.Mvvm"),
            "https://github.com/CommunityToolkit/dotnet"),
        new("lucide", "ISC",
            L("Tools.Dependency.VectorIcons"),
            "https://lucide.dev/"),
        new("simple-icons", "CC0 1.0",
            L("Tools.Dependency.BrandIcons"),
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
        L("Tools.Records.Summary", AppServices.Records.TotalCount,
          AppServices.Records.SuccessCount, AppServices.Records.FailureCount);

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
        RefreshDiagnosticsText();   // 报告正文里也含记录数字，一并重算
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
                : L("Tools.UnverifiedIntro") + string.Join("\n", notes);
            HasVerificationNotes = notes.Count > 0;

            ProbeTimeText = L("Tools.ProbeTime", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            RefreshDiagnosticsText();

            // 记录数字也随这次加载一起刷新：RecordsPathText / RecordsCountText 是计算属性，
            // 不会自己收到变更通知（见 RefreshRecordsView 的说明）。
            OnPropertyChanged(nameof(RecordsPathText));
            OnPropertyChanged(nameof(RecordsCountText));

            HasInitializationWarning = AppServices.LastError is not null;
            InitializationWarningText = AppServices.LastError ?? "";

            StatusMessage = refresh
                ? L("Tools.ProbeRedone", SourceRows.Count)
                : L("Tools.ProbeLoaded", SourceRows.Count);
            return true;
        }
        catch (Exception ex)
        {
            // 诊断失败本身也是结论：把原因显示出来，并保留上一份报告（不把页面清空）。
            ErrorMessage = L("Tools.ProbeFailed", ex.Message);
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
        sb.AppendLine(L("Tools.Report.HomeDir", HomeDirectoryText));
        sb.AppendLine(L("Tools.Report.RecordPath", RecordsPathText));
        sb.AppendLine(L("Tools.Report.RecordCount", RecordsCountText));
        sb.AppendLine(L("Tools.Report.DebugOut", L(TerminalBridge.DebugMode ? "Common.On" : "Common.Off")));
        if (AppServices.LastError is { } initError) sb.AppendLine(L("Tools.Report.InitWarning", initError));
        sb.AppendLine();
        sb.AppendLine(L("Tools.Report.SourceDetail"));

        if (SourceRows.Count == 0)
        {
            sb.AppendLine(L("Tools.Report.NotProbed"));
            return sb.ToString().TrimEnd();
        }

        foreach (var row in SourceRows)
        {
            sb.AppendLine(L("Tools.Report.SourceRow", row.Title, row.StateText,
                L(row.SupportedHere ? "Common.Yes" : "Common.No")));
            sb.AppendLine(L("Tools.Report.CliRow", row.CliPathText, row.CliVersionText));
            sb.AppendLine(L("Tools.Report.InstallDirRow", row.InstallDirectory));
            if (row.HasDetail) sb.AppendLine(L("Tools.Report.DetailRow", row.Detail));
            if (row.HasVerificationNote) sb.AppendLine(L("Tools.Report.VerificationRow", row.VerificationNote));
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHostText()
    {
        // macOS/Linux 上运行的是 CrossOver/Wine 托管的 Windows 目标，
        // 探测结论（尤其是路径与 CLI 发现）与真机 Windows 不同，必须写清楚。
        if (SystemInfo.IsWindows)
            return L(OperatingSystem.IsWindows() ? "Tools.Platform.WindowsNative" : "Tools.Platform.Wine");
        return L("Tools.Platform.OtherHost", RuntimeInformation.RuntimeIdentifier);
    }

    private static string BuildVersionText()
    {
        // 无 UI 宿主（无头测试/设计器）时拿不到入口程序集，故用程序集名兜底，
        // 并且真取不到版本时也不显示 "0.0.0.0.0"。
        try
        {
            var assembly = Assembly.GetEntryAssembly();
            var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return L("Tools.AppVersion", informational.Split('+')[0]);   // 去掉 SourceLink 附加的 +commit 后缀

            var version = assembly?.GetName().Version?.ToString(3);
            return string.IsNullOrWhiteSpace(version) ? L("Tools.AppName") : L("Tools.AppVersion", version);
        }
        catch (Exception)
        {
            return "UMango";
        }
    }

    private static string BuildBuildInfoText()
    {
        var framework = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(framework)) parts.Add(L("Tools.TargetFramework", framework));
        parts.Add(L("Tools.Runtime", RuntimeInformation.FrameworkDescription));
        parts.Add(L("Tools.OS", RuntimeInformation.OSDescription));
        parts.Add(L("Tools.ProcessArch", RuntimeInformation.ProcessArchitecture));

        try
        {
            var location = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(location)) parts.Add(L("Tools.Executable", location));
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
            return L("Tools.Unavailable");
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
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

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
        StateBrushKey = StateToBrushKey(categoryState);
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
        CliPathText = string.IsNullOrWhiteSpace(availability.CliPath) ? L("Tools.CliNotFound") : availability.CliPath!;
        CliVersionText = string.IsNullOrWhiteSpace(availability.CliVersion) ? L("Tools.CliVersionUnknown") : availability.CliVersion!;

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

    /// <summary>
    /// 状态颜色对应的**主题 key**（不是画刷）。
    /// 各壳用各自的资源查找把它解析成画刷，因此颜色仍随主题（浅/深、PCL/Fluent）变化。
    /// </summary>
    public string StateBrushKey { get; }

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
        AvailabilityState.Available => L("State.Available"),
        AvailabilityState.Degraded => L("State.Degraded"),
        AvailabilityState.UnsupportedPlatform => L("State.UnsupportedPlatform"),
        AvailabilityState.NotInstalled => L("State.NotInstalled"),
        _ => L("State.Unreachable")
    };

    /// <summary>
    /// 状态 → 主题 key。
    /// 键名与 WPF 侧原实现完全一致（BrC3/BrC6/BrRed/BrG3），两壳观感才一致。
    /// </summary>
    private static string StateToBrushKey(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => "BrC3",
        AvailabilityState.Degraded => "BrC6",
        AvailabilityState.NotInstalled or AvailabilityState.Unreachable => "BrRed",
        _ => "BrG3"
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
        LicenseText = LocalizationService.Text("Tools.License", license);
    }

    public string Name { get; }
    public string License { get; }
    public string LicenseText { get; }
    public string Purpose { get; }
    public string Url { get; }
}
