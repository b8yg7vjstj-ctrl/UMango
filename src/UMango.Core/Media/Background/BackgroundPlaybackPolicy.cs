// SPDX-License-Identifier: Apache-2.0
//
// 动态背景：显示策略（什么条件下播、什么条件下停、什么条件下暂停）。
//
// 采用自 PCL-CE 的设计语义：
//   原始文件：Plain Craft Launcher 2/Modules/ModVideoBack.cs（172 行）
//   （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
//   不复制其代码；本文件为独立重写。
//
// PCL 的原版规则：
//   · `isMinimized`（窗口最小化）→ 暂停；
//   · `IsGaming`（游戏运行中）→ 暂停，除非 `ForcePlay`（用户强制播放）；
//   · `ForcePlay` 为真时，无论是否在游戏中都播；
//   · 停止 = `Source = null` + `Stop()` + `Position = 0`；
//   · 视频失败按 `0xC00D109B` 判定「不是 H.264 格式」。
//
// UMango 的差异：
//   1. PCL 的"是否在游戏中"是 MC 专属状态 → UMango 没有 MC，改为**「忙碌」概念**
//      （正在执行耗时任务时暂停动态背景），语义更通用；
//   2. PCL 的暂停/播放由三处事件驱动、状态散落 → UMango 用**一个纯函数**算出
//      当前应该处于什么状态，状态机只有一份真相；
//   3. 失败分类显式化（PCL 只在提示文案里区分 0xC00D109B）。

namespace UMango.Core.Media.Background;

/// <summary>动态背景的运行状态。</summary>
public enum BackgroundPlaybackState
{
    /// <summary>未播放（没有背景内容，或已停止）。</summary>
    Stopped,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停。</summary>
    Paused,
}

/// <summary>
/// 影响动态背景的环境状态。
/// </summary>
public sealed record BackgroundEnvironment
{
    /// <summary>窗口是否已最小化。</summary>
    public bool IsMinimized { get; init; }

    /// <summary>
    /// 是否处于「忙碌」状态（正在执行耗时任务）。
    /// 对应 PCL 的 <c>IsGaming</c> —— PCL 指的是游戏运行中，UMango 泛化为忙碌。
    /// </summary>
    public bool IsBusy { get; init; }

    /// <summary>
    /// 是否强制播放（用户明确要求无视忙碌状态继续播）。
    /// 对应 PCL 的 <c>ForcePlay</c>。
    /// </summary>
    public bool ForcePlay { get; init; }
}

/// <summary>
/// 动态背景的显示策略：**纯函数**，输入环境 + 当前状态，输出应当处于的状态。
///
/// 做成纯函数的好处是策略能被单测穷举，而不用真的起一个 MediaElement。
/// </summary>
public static class BackgroundPlaybackPolicy
{
    /// <summary>
    /// 算出当前应当处于的状态。
    /// </summary>
    /// <param name="hasContent">是否有背景内容可播。</param>
    /// <param name="environment">环境状态。</param>
    /// <param name="current">当前状态。</param>
    /// <returns>应当处于的状态。</returns>
    public static BackgroundPlaybackState Resolve(
        bool hasContent,
        BackgroundEnvironment environment,
        BackgroundPlaybackState current)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // 没有内容 → 停。
        if (!hasContent) return BackgroundPlaybackState.Stopped;

        // 已经停了且没有内容要恢复 → 保持停止。
        if (current == BackgroundPlaybackState.Stopped) return BackgroundPlaybackState.Stopped;

        // 窗口最小化 → 暂停（PCL 行为）。
        if (environment.IsMinimized) return BackgroundPlaybackState.Paused;

        // 忙碌时：只有强制播放才继续（PCL 行为）。
        if (environment.IsBusy && !environment.ForcePlay) return BackgroundPlaybackState.Paused;

        return BackgroundPlaybackState.Playing;
    }

    /// <summary>
    /// 是否应当开始播放（PCL 的 <c>VideoPlay</c> 的判定条件）。
    /// </summary>
    /// <param name="hasContent">是否有背景内容可播。</param>
    /// <param name="environment">环境状态。</param>
    /// <returns>是否应当播放。</returns>
    public static bool ShouldPlay(bool hasContent, BackgroundEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!hasContent) return false;
        if (environment.IsMinimized) return false;
        if (environment.IsBusy && !environment.ForcePlay) return false;
        return true;
    }
}

/// <summary>背景内容加载失败的分类。</summary>
public enum BackgroundFailureKind
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>文件不存在。</summary>
    FileNotFound,

    /// <summary>格式不支持（解码器不认识）。</summary>
    UnsupportedFormat,

    /// <summary>
    /// 视频不是 H.264（AVC）格式。
    /// 对应 PCL 判定的 <c>0xC00D109B</c>。
    /// </summary>
    NotH264,

    /// <summary>其它播放失败。</summary>
    PlaybackFailed,
}

/// <summary>
/// 背景内容失败的分类器。
///
/// PCL 在 UI 层直接用异常消息里的 <c>0xC00D109B</c> 判断是不是 H.264 问题；
/// 这里把判断提到 Core，并**同时**看异常类型与消息，两条路都认。
/// </summary>
public static class BackgroundFailureClassifier
{
    /// <summary>WPF MediaFailed 里代表「不是 H.264」的错误码。</summary>
    public const string NotH264Code = "0xC00D109B";

    /// <summary>
    /// 分类一个加载失败。
    /// </summary>
    /// <param name="exception">异常（可为 null）。</param>
    /// <param name="fileName">出问题的文件名（用于日志）。</param>
    public static BackgroundFailureKind Classify(Exception? exception, string? fileName = null)
    {
        if (exception is null) return BackgroundFailureKind.None;

        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return BackgroundFailureKind.FileNotFound;

        var message = exception.Message ?? string.Empty;

        if (message.Contains(NotH264Code, StringComparison.OrdinalIgnoreCase))
            return BackgroundFailureKind.NotH264;

        // WPF 的 MediaFailed 常见文案里带 HRESULT（形如 "0xC00D11B4"）。
        // 只认明确的几个：解码器缺失 / 不支持的编码。
        if (message.Contains("0xC00D5212", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("0xC00D10D9", StringComparison.OrdinalIgnoreCase))
            return BackgroundFailureKind.UnsupportedFormat;

        if (exception is NotSupportedException or FormatException)
            return BackgroundFailureKind.UnsupportedFormat;

        return BackgroundFailureKind.PlaybackFailed;
    }
}
