using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.Download;
using UMango.Core.Platform;
using UMango.Core.Terminal;
using UMango.Core.Tools;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 工具页的**可执行动作**：垃圾清理 + 自定义下载。
///
/// 这两块对应 PCL-CE `PageToolsTest.xaml.cs` 里的「垃圾清理」与「自定义下载」，
/// 但本类刻意把两件事都做成"薄壳"：
///
///   · **校验与计算全在 Core**（<see cref="CustomDownloadRequest"/> / <see cref="CleanupPlanner"/>），
///     这里只做界面状态与文件选择对话框 —— 因为 WPF 类型在 macOS 上不存在，
///     逻辑留在 App 层就等于放弃单测覆盖。
///   · 下载走 Core 的 <see cref="DownloadEngineRegistry"/>（aria2 / eD2k / 内置 HttpClient），
///     与下载器页共用同一套引擎与降级策略，不另起一条链路。
/// </summary>
public sealed partial class ToolsActionsViewModel : ObservableObject
{
    /// <summary>清理统计（未执行清理前也算一次，让用户先看到"有多少东西可清"）。</summary>
    public ObservableCollection<CleanupItem> CleanupItems { get; } = [];

    /// <summary>清理前的摘要文案。</summary>
    [ObservableProperty]
    private string _cleanupSummary = "";

    /// <summary>清理进行中（禁用按钮，避免并发执行两次清理）。</summary>
    [ObservableProperty]
    private bool _isCleaning;

    /// <summary>自定义下载的 URL。</summary>
    [ObservableProperty]
    private string _customDownloadUrl = "";

    /// <summary>自定义下载的文件名（可空 → 从 URL 推断）。</summary>
    [ObservableProperty]
    private string _customDownloadFileName = "";

    /// <summary>自定义下载的目标目录（可空 → 用默认下载目录）。</summary>
    [ObservableProperty]
    private string _customDownloadDirectory = "";

    /// <summary>下载进行中。</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>下载进度（0~1；拿不到就是 null，不编造）。</summary>
    [ObservableProperty]
    private double? _downloadProgress;

    /// <summary>统一的中性提示。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>面向用户的一句话错误。</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>清理目标 → 目录 的映射（App 侧注入真实路径）。</summary>
    private readonly Func<IReadOnlyDictionary<CleanupTarget, string>> _cleanupTargets;

    /// <summary>下载引擎注册表（App 侧注入，避免在 ViewModel 里 new）。</summary>
    private readonly DownloadEngineRegistry _engines;

    /// <summary>终端通道 id（自定义下载的过程输出走这个通道）。</summary>
    private const string ChannelId = "tools";

    private CancellationTokenSource? _downloadCts;

    public ToolsActionsViewModel(
        DownloadEngineRegistry engines,
        Func<IReadOnlyDictionary<CleanupTarget, string>>? cleanupTargets = null)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _cleanupTargets = cleanupTargets ?? (() => DefaultCleanupTargets);
        RefreshCleanupSummary();
    }

    /// <summary>默认的清理目标（用 Core 的路径快照）。</summary>
    public static IReadOnlyDictionary<CleanupTarget, string> DefaultCleanupTargets
    {
        get
        {
            var paths = AppPaths.Current;
            return new Dictionary<CleanupTarget, string>
            {
                [CleanupTarget.TempDownloads] = paths.TempDownloadsDirectory,
                [CleanupTarget.Cache] = paths.CacheDirectory,
                [CleanupTarget.Logs] = paths.LogsDirectory,
                [CleanupTarget.UpdateStaging] = paths.UpdateStagingDirectory,
                [CleanupTarget.CrashReports] = Path.Combine(paths.LogsDirectory, "crashes"),
            };
        }
    }

    /// <summary>重新统计可清理内容（不删除任何东西）。</summary>
    [RelayCommand]
    public void RefreshCleanupSummary()
    {
        var plan = CleanupPlanner.Plan(_cleanupTargets());
        CleanupItems.Clear();
        foreach (var item in plan.Items) CleanupItems.Add(item);

        CleanupSummary = plan.TotalFileCount == 0
            ? L("Tools.Cleanup.Nothing")
            : L("Tools.Cleanup.Summary", plan.TotalFileCount, FormatBytes(plan.TotalBytes));
    }

    /// <summary>执行清理（删掉统计到的内容，保留目录本身）。</summary>
    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (IsCleaning) return;
        IsCleaning = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var plan = CleanupPlanner.Plan(_cleanupTargets());

            // 清理是磁盘 I/O，放到后台线程，别把 UI 线程卡住。
            var (files, bytes) = await Task.Run(() => CleanupPlanner.Execute(plan)).ConfigureAwait(true);

            RefreshCleanupSummary();
            StatusMessage = L("Tools.Cleanup.Done", files, FormatBytes(bytes));
        }
        catch (Exception ex)
        {
            ErrorMessage = L("Tools.Cleanup.Failed", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    /// <summary>开始自定义下载。</summary>
    [RelayCommand]
    private async Task CustomDownloadAsync()
    {
        if (IsDownloading) return;

        ErrorMessage = null;
        StatusMessage = null;
        DownloadProgress = null;

        var validation = CustomDownloadRequest.Validate(
            CustomDownloadUrl, CustomDownloadFileName, CustomDownloadDirectory,
            AppPaths.Current.DefaultDownloadDirectory);

        if (!validation.IsValid)
        {
            ErrorMessage = L(validation.ErrorKey ?? "Validation.InvalidUrl");
            return;
        }

        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        try
        {
            var bridge = TerminalBridge.For(ChannelId);
            var request = new DownloadRequest(validation.Url!, validation.Directory!, validation.FileName!);

            var result = await _engines.DownloadAsync(request, bridge, p =>
            {
                if (p.Fraction is { } f) DownloadProgress = f;
            }, ct).ConfigureAwait(true);

            if (result.Success)
            {
                StatusMessage = L("Tools.CustomDownload.Done", result.OutputPath ?? validation.FileName!);
            }
            else
            {
                ErrorMessage = L("Tools.CustomDownload.Failed", result.Message ?? result.ErrorCode ?? "未知错误");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L("Tools.CustomDownload.Canceled");
        }
        catch (Exception ex)
        {
            ErrorMessage = L("Tools.CustomDownload.Failed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>取消自定义下载。</summary>
    [RelayCommand]
    private void CancelCustomDownload() => _downloadCts?.Cancel();

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string L(string key, params object?[] args)
        => Core.Localization.LocalizationService.Text(key, args);
}
