// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐：播放状态机与错误分类。
//
// 采用自 PCL-CE（PCL-Community/PCL-CE）的设计语义：
//   原始文件：Plain Craft Launcher 2/Modules/ModMusic.cs
//   （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
//   不复制其代码；本文件为独立重写。
//
// PCL 原版的错误处理值得注意：它**不是**简单地"播放失败"，
// 而是按异常特征分类，给用户不同的提示：
//   · MmException 且含 "AlreadyAllocated"  → 设备被占用
//   · MmException 且含 "NoDriver"/"BadDeviceId" → 设备已变化
//   · 消息含 "Got a frame at sample rate"  → 不支持播放中变更属性
//   · 扩展名不在 .wav/.mp3/.flac 或含 "0xC00D36C4" → 格式不支持
//   · 其它 → 通用播放失败
// 本文件把这一分类做成**纯函数**（可单测），不依赖 NAudio 的异常类型。

namespace UMango.Core.Media.Music;

/// <summary>播放状态。</summary>
public enum MusicPlaybackState
{
    /// <summary>停止。</summary>
    Stopped,

    /// <summary>播放中。</summary>
    Playing,

    /// <summary>已暂停。</summary>
    Paused,
}

/// <summary>播放失败的原因分类（决定给用户什么提示）。</summary>
public enum MusicFailureKind
{
    /// <summary>不是失败（成功）。</summary>
    None = 0,

    /// <summary>音频设备被其他程序占用。</summary>
    DeviceBusy,

    /// <summary>音频设备已发生变化（拔了/换了设备）。</summary>
    DeviceChanged,

    /// <summary>不支持在播放过程中变更音频属性。</summary>
    PropertyChangeUnsupported,

    /// <summary>不支持的文件格式。</summary>
    UnsupportedFormat,

    /// <summary>文件不存在。</summary>
    FileNotFound,

    /// <summary>其它播放失败。</summary>
    PlaybackFailed,
}

/// <summary>
/// 播放失败的原因分类器（纯函数）。
/// </summary>
public static class MusicFailureClassifier
{
    /// <summary>
    /// 根据异常与文件扩展名判断失败类别。
    /// </summary>
    /// <param name="exception">播放时抛出的异常（null = 没有异常）。</param>
    /// <param name="fileName">文件扩展名（含点，小写）。留空则不看格式。</param>
    public static MusicFailureKind Classify(Exception? exception, string? fileName = null)
    {
        if (exception is null) return MusicFailureKind.None;

        // 文件不存在：PCL 单独处理（会触发重新扫描列表）。
        if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
            return MusicFailureKind.FileNotFound;

        var message = exception.Message;

        // 设备被占用 / 设备已变化。
        if (message.Contains("AlreadyAllocated", StringComparison.OrdinalIgnoreCase))
            return MusicFailureKind.DeviceBusy;
        if (message.Contains("NoDriver", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase))
            return MusicFailureKind.DeviceChanged;

        // 不支持播放中变更属性。
        if (message.Contains("Got a frame at sample rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not support changes to", StringComparison.OrdinalIgnoreCase))
            return MusicFailureKind.PropertyChangeUnsupported;

        // 格式不支持：扩展名不在白名单里，或命中 WMF 的 0xC00D36C4。
        if (!string.IsNullOrEmpty(fileName)
            && !MusicTrack.SupportedExtensions.Contains(fileName))
            return MusicFailureKind.UnsupportedFormat;
        if (message.Contains("0xC00D36C4", StringComparison.OrdinalIgnoreCase))
            return MusicFailureKind.UnsupportedFormat;

        return MusicFailureKind.PlaybackFailed;
    }
}

/// <summary>
/// 背景音乐播放器的**状态机**（纯逻辑，不碰音频设备）。
///
/// 播放器的对外语义（播放/暂停/继续/下一曲/刷新）都落在这台状态机上，
/// 真正的解码/输出交给实现（App 层的 NAudio 实现）。
/// </summary>
public sealed class MusicPlaybackController
{
    private readonly MusicLibrary _library;
    private double _progress;

    /// <summary>创建控制器。</summary>
    public MusicPlaybackController(MusicLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <summary>当前状态。</summary>
    public MusicPlaybackState State { get; private set; } = MusicPlaybackState.Stopped;

    /// <summary>当前曲目（未播放时为 null）。</summary>
    public MusicTrack? Current { get; private set; }

    /// <summary>
    /// 当前播放进度（0~1）。
    ///
    /// 由播放实现（App 层）回报进度；写入时夹到 [0,1]。
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            var clamped = double.IsNaN(value) ? 0d : Math.Clamp(value, 0d, 1d);
            if (Math.Abs(clamped - _progress) < double.Epsilon) return;
            _progress = clamped;
            StateChanged?.Invoke();
        }
    }

    /// <summary>音量（0~1）。</summary>
    public double Volume { get; set; } = 0.5;

    /// <summary>状态变化时触发（UI 刷新用）。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 播放队列中的下一首（不改变状态）。
    /// </summary>
    /// <param name="shuffle">队列空时重建是否随机。</param>
    public MusicTrack? PeekNext(bool shuffle) => _library.PeekNext(shuffle);

    /// <summary>
    /// 开始播放（队列里没有就什么都不做）。
    /// </summary>
    /// <param name="shuffle">队列空时重建是否随机。</param>
    /// <param name="autoplay">
    /// 首次加载且用户未启用自动播放时，播起来之后立刻暂停（PCL 的
    /// <c>isFirstLoad</c> 语义）。
    /// </param>
    /// <returns>开始播放的曲目；没有可播的返回 null。</returns>
    public MusicTrack? Play(bool shuffle, bool autoplay = true)
    {
        var next = _library.DequeueNext(shuffle);
        if (next is null)
        {
            State = MusicPlaybackState.Stopped;
            Current = null;
            Progress = 0d;
            StateChanged?.Invoke();
            return null;
        }

        Current = next;
        Progress = 0d;
        State = autoplay ? MusicPlaybackState.Playing : MusicPlaybackState.Paused;
        StateChanged?.Invoke();
        return next;
    }

    /// <summary>
    /// 暂停播放。
    /// </summary>
    /// <returns>是否真的暂停了（不在播放状态时返回 false）。</returns>
    public bool Pause()
    {
        if (State != MusicPlaybackState.Playing) return false;
        State = MusicPlaybackState.Paused;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 继续播放。
    /// </summary>
    /// <returns>是否真的继续了（已在播放或没有曲目时返回 false）。</returns>
    public bool Resume()
    {
        if (State == MusicPlaybackState.Playing || Current is null) return false;
        State = MusicPlaybackState.Playing;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 停止播放并清空当前曲目。
    /// </summary>
    public void Stop()
    {
        State = MusicPlaybackState.Stopped;
        Current = null;
        Progress = 0d;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 播放下一曲。
    /// </summary>
    /// <param name="shuffle">队列空时重建是否随机。</param>
    /// <returns>开始播放的曲目；没有可播的返回 null。</returns>
    public MusicTrack? PlayNext(bool shuffle) => Play(shuffle);

    /// <summary>
    /// 重播当前曲目（PCL 的「只有一首时右键 = 从头播放」语义）。
    /// </summary>
    /// <returns>是否重播成功。</returns>
    public bool ReplayCurrent()
    {
        if (Current is null) return false;
        Progress = 0d;
        State = MusicPlaybackState.Playing;
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 处理一次播放失败：把曲目从库里剔除并推进到下一曲。
    ///
    /// PCL 的原版在捕获异常后**从列表里移除无效文件**，然后尝试下一首。
    /// </summary>
    /// <param name="exception">异常（null = 没异常，什么都不做）。</param>
    /// <param name="shuffle">队列空时重建是否随机。</param>
    /// <returns>失败分类；没有异常返回 <see cref="MusicFailureKind.None"/>。</returns>
    public MusicFailureKind HandleFailure(Exception? exception, bool shuffle)
    {
        if (exception is null) return MusicFailureKind.None;

        var kind = MusicFailureClassifier.Classify(exception, Current?.Extension);

        if (Current is not null) _library.Remove(Current);

        Current = null;
        Progress = 0d;
        State = MusicPlaybackState.Stopped;
        StateChanged?.Invoke();

        // 文件不存在 → 重新扫描列表（PCL 的 `MusicRefreshPlay(true)`）；
        // 其它失败 → 直接放下一首。
        if (kind == MusicFailureKind.FileNotFound) return kind;

        Play(shuffle);
        return kind;
    }
}
