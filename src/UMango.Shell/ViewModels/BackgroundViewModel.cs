// SPDX-License-Identifier: Apache-2.0
//
// 动态背景的 ViewModel：把 Core 的判定与 App 的显示端粘起来。
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml.cs`
// 的 `BackgroundRefresh`（受限许可目录，只借鉴语义、不复制代码）：
//   · 目录扫描 → 排除 .ini/.db → 挑一个 → 先试图片、失败退当视频；
//   · 目录为空时收起背景层；
//   · 刷新时给用户提示（成功/无内容/失败）。
//
// UMango 的差异：
//   · 目录可配置（PCL 写死 <exe>/PCL/Pictures）；
//   · 视频可用性受设置控制（PCL 只有图片，没有「要不要视频」这个选项）。
//
// 本类**不碰任何 WPF 控件** —— 显示交给 `BackgroundPresenter`，
// 这样「什么时候刷新/选哪个/失败怎么办」都能单测。

using UMango.Core.Configuration;
using UMango.Core.Media.Background;

namespace UMango.Shell.ViewModels;

/// <summary>动态背景的状态与动作。</summary>
public sealed class BackgroundViewModel
{
    private readonly SettingsService _settings;
    private readonly string? _directoryOverride;
    private readonly Random? _random;

    /// <summary>内容库（扫描结果）。</summary>
    public BackgroundLibrary Library { get; } = new();

    /// <summary>
    /// 构造 ViewModel。
    /// </summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="directoryOverride">背景目录覆盖值（给 null 则从设置里读）。</param>
    /// <param name="random">随机源（给 null 用 <see cref="Random.Shared"/>）。</param>
    public BackgroundViewModel(
        SettingsService settings,
        string? directoryOverride = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _directoryOverride = directoryOverride;
        _random = random;
    }

    /// <summary>最近一次刷新的结果说明（供界面显示）。</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>最近一次刷新的失败分类。</summary>
    public BackgroundFailureKind LastFailure { get; private set; } = BackgroundFailureKind.None;

    /// <summary>背景是否启用（读设置）。</summary>
    public bool Enabled => _settings.Get<bool>(UiSettings.BackgroundEnabledKey);

    /// <summary>是否允许视频背景（读设置）。</summary>
    public bool VideoEnabled => _settings.Get<bool>(UiSettings.BackgroundVideoEnabledKey);

    /// <summary>忙碌时是否强制播放（读设置）。</summary>
    public bool ForcePlay => _settings.Get<bool>(UiSettings.BackgroundForcePlayKey);

    /// <summary>背景目录（覆盖值优先，否则读设置；再否则用程序目录下的 Pictures）。</summary>
    public string Directory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_directoryOverride)) return _directoryOverride;

            var configured = _settings.Get<string>(UiSettings.BackgroundDirectoryKey);
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            return Path.Combine(AppContext.BaseDirectory, "Pictures");
        }
    }

    /// <summary>
    /// 重新扫描目录并挑一个背景。
    /// </summary>
    /// <returns>挑到的文件路径；没有内容时返回 null。</returns>
    public string? Refresh()
    {
        LastFailure = BackgroundFailureKind.None;

        var count = Library.Reload(Directory);

        if (count == 0)
        {
            StatusMessage = "背景目录里没有可用的内容。";
            return null;
        }

        var picked = Library.PickCandidate(_random);
        StatusMessage = picked is null
            ? "背景目录里没有可用的内容。"
            : $"已选中背景：{Path.GetFileName(picked)}";
        return picked;
    }

    /// <summary>
    /// 把当前环境套用到播放策略，算出应当处于的状态。
    /// </summary>
    /// <param name="current">当前状态。</param>
    /// <param name="isMinimized">窗口是否最小化。</param>
    /// <param name="isBusy">是否在执行耗时任务。</param>
    public BackgroundPlaybackState ResolveState(
        BackgroundPlaybackState current,
        bool isMinimized,
        bool isBusy)
    {
        var environment = new BackgroundEnvironment
        {
            IsMinimized = isMinimized,
            IsBusy = isBusy,
            ForcePlay = ForcePlay,
        };

        var hasContent = Library.Candidates.Count > 0;
        return BackgroundPlaybackPolicy.Resolve(hasContent, environment, current);
    }

    /// <summary>记录一次失败（供界面提示）。</summary>
    public void ReportFailure(BackgroundFailureKind kind)
    {
        LastFailure = kind;
        StatusMessage = kind switch
        {
            BackgroundFailureKind.NotH264 => "该视频可能不是 H.264（AVC）格式，无法播放。",
            BackgroundFailureKind.UnsupportedFormat => "该文件的格式不受支持。",
            BackgroundFailureKind.FileNotFound => "背景文件已不存在。",
            BackgroundFailureKind.PlaybackFailed => "背景播放失败。",
            _ => StatusMessage,
        };
    }
}
