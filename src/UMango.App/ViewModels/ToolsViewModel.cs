using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Services;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using UMango.Core.Terminal;

namespace UMango.App.ViewModels;

/// <summary>
/// 工具页：系统判断信息 + 包源可用性明细（含"未验证"标注）。
///
/// 这一页是排查问题的入口，所以两条原则：
///   · **如实**：未在真机验证过的适配器、被平台门控的包源、探测失败的原因都照原样显示
///     （<see cref="PackageSourceCategory.VerificationNote"/> 就是 Core 为此提供的字段）；
///   · **自身不脆**：诊断过程出错（探测抛异常）时把可读原因显示出来，而不是让页面空白或崩溃。
///
/// 构造时就会主动加载一次：页面可能只做绑定、不写代码后置，
/// 若必须由页面显式调用初始化，很容易出现"诊断页永远是空的"。
/// <see cref="InitializeAsync"/> 仍然对外提供且幂等。
/// </summary>
public partial class ToolsViewModel : ObservableObject
{
    private readonly ObservableCollection<PackageSourceCategory> _allSources = [];

    /// <summary>全部包源（不做可用性过滤，不可用的也要能看到原因）。</summary>
    public IReadOnlyList<PackageSourceCategory> AllSources => _allSources;

    /// <summary>系统判断摘要，例如 "macOS arm64"。</summary>
    public string PlatformText { get; } = SystemInfo.Describe();

    /// <summary>架构（x64 / arm64）。</summary>
    public string ArchitectureText { get; } = SystemInfo.Architecture;

    /// <summary>用户主目录（各包管理器的安装目录通常在其下）。</summary>
    public string HomeDirectoryText { get; } = SystemInfo.HomeDirectory;

    /// <summary>
    /// 可直接展示的多行诊断报告（平台/记录文件/终端通道/逐包源明细）。
    /// 提供文本形态是为了让诊断页绑定一个只读文本框就能看全，
    /// 不必为每个字段单独写模板。
    /// </summary>
    [ObservableProperty]
    private string _systemReport = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    private bool _initialized;
    private bool _loading;

    public ToolsViewModel()
    {
        SystemReport = BuildReport();   // 先给一份不含包源探测结果的报告，避免首帧空白
        _ = InitializeAsync();          // 故意不 await：构造不能阻塞 UI，加载失败也已在内部处理
    }

    /// <summary>加载包源明细（幂等；页面也可在 Loaded 时显式等待它）。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized || _loading) return;
        _initialized = await LoadSourcesAsync(refresh: false);
    }

    /// <summary>重新探测全部包源并刷新明细。</summary>
    [RelayCommand]
    private async Task RefreshAsync() => await LoadSourcesAsync(refresh: true);

    private async Task<bool> LoadSourcesAsync(bool refresh)
    {
        if (_loading) return false;
        _loading = true;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var list = await AppServices.Registry.GetCategoriesAsync(refresh);

            _allSources.Clear();
            foreach (var category in list) _allSources.Add(category);

            SystemReport = BuildReport();
            StatusMessage = refresh
                ? $"已重新探测 {list.Count} 个包源。"
                : $"已加载 {list.Count} 个包源。";
            return true;
        }
        catch (Exception ex)
        {
            // 诊断页不能因为"诊断失败"而失效：显示原因，保留上一份报告。
            ErrorMessage = $"包源探测失败：{ex.Message}";
            return false;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    /// <summary>拼装诊断报告。只在构造与刷新时调用，不做成属性 getter（避免绑定每帧拼字符串）。</summary>
    private string BuildReport()
    {
        var sb = new StringBuilder();
        var records = AppServices.Records;

        sb.AppendLine($"平台：{PlatformText}");
        sb.AppendLine($"架构：{ArchitectureText}");
        sb.AppendLine($"主目录：{HomeDirectoryText}");
        sb.AppendLine($"记录文件：{records.StorePath}");
        sb.AppendLine($"记录条数：{records.TotalCount}（成功 {records.SuccessCount} / 失败 {records.FailureCount}）");

        var channels = TerminalBridge.ChannelNames;
        sb.AppendLine($"终端通道：{(channels.Count == 0 ? "（无）" : string.Join("、", channels))}");
        sb.AppendLine($"调试模式：{(TerminalBridge.DebugMode ? "开启" : "关闭")}");

        // 初始化阶段被兜底吞掉的错误（包源注册表/记录存储）——不看这里就只能靠猜。
        if (AppServices.LastError is { } initError) sb.AppendLine($"初始化警告：{initError}");

        sb.AppendLine();
        if (_allSources.Count == 0)
        {
            sb.AppendLine("包源明细：（尚未探测，点「重新探测」获取）");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("包源明细：");
        foreach (var category in _allSources)
        {
            sb.AppendLine($"· {category.Title}｜安装目录：{category.Subtitle}");
            sb.AppendLine($"    状态：{StateText(category.AvailabilityState)}"
                          + $"｜当前平台支持：{(category.SupportedHere ? "是" : "否")}"
                          + $"｜可用：{(category.Available ? "是" : "否")}");
            if (!string.IsNullOrWhiteSpace(category.VerificationNote))
                sb.AppendLine($"    未验证：{category.VerificationNote}");
            if (!string.IsNullOrWhiteSpace(category.Detail))
                sb.AppendLine($"    明细：{category.Detail}");
            if (!string.IsNullOrWhiteSpace(category.InstallHint))
                sb.AppendLine($"    安装指引：{category.InstallHint}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>可用性状态 → 中文说明。界面不直接显示枚举名，否则用户看不懂。</summary>
    private static string StateText(AvailabilityState state) => state switch
    {
        AvailabilityState.Available => "可用",
        AvailabilityState.Degraded => "可用（降级）",
        AvailabilityState.UnsupportedPlatform => "当前平台不支持",
        AvailabilityState.NotInstalled => "未安装命令行工具",
        _ => "不可达"
    };
}
