// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的外壳能力实现。
//
// 与 WPF 侧的对应关系：
//   WPF      : src/UMango.App/Services/WpfClipboardService.cs
//   Avalonia : 本文件
//
// 三个能力在两个壳里的可得性：
//   ① 剪贴板 —— 两边都有（但 Avalonia 的剪贴板是**异步**的，见下）。
//   ② 音乐   —— WPF 用 NAudio；Avalonia 侧暂不引音频库（见 MusicOutput 的说明）。
//   ③ 灰度   —— WPF 能改画刷；Avalonia 无 ColorMatrix 等价物（见 GrayModeApplier 的说明）。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using UMango.Shell.Capabilities;

namespace UMango.Avalonia.Services;

/// <summary>
/// Avalonia 剪贴板。
///
/// <para>
/// **与 WPF 版的区别**：Avalonia 的 <c>Clipboard.SetTextAsync</c> 是异步的
/// （要等宿主回复），而 Shell 的 <see cref="IClipboardService"/> 是同步的 ——
/// 因为调用方（反馈页「复制」）是同步的按钮处理器。
/// </para>
///
/// <para>
/// 做法：同步接口里发起异步写并**不等待**（fire-and-forget）。
/// 复制是「尽力而为」的操作，用户不会因为几十毫秒的延迟而察觉失败；
/// 真正要等结果的地方（如果有）应另开异步接口。
/// </para>
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    /// <summary>把文本写入剪贴板（尽力而为；失败不抛）。</summary>
    public void SetText(string text)
    {
        try
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var window = lifetime?.MainWindow;
            if (window is null) return;

            var clipboard = window.Clipboard;
            if (clipboard is null) return;

            // 故意不等：调用方是同步的按钮处理器。
            _ = clipboard.SetTextAsync(text ?? string.Empty);
        }
        catch (Exception)
        {
            // 剪贴板不可用（无 X11/剪贴板服务）时不该让界面崩。
        }
    }
}

/// <summary>
/// Avalonia 侧的灰度施加器。
///
/// <para>
/// **当前是空实现（明确降级）**：WPF 能通过替换资源字典里的画刷来脱色，
/// 而 Avalonia **没有 ColorMatrix 等价物**（Avalonia 11 的效果管线里没有
/// 与 WPF <c>ColorMatrixEffect</c> 对应的节点，也没有像
/// <see cref="ColorizeEffect"/> 那样能整体脱色的内置效果）。
/// </para>
///
/// <para>
/// 之所以**显式**留一个空实现而不是不注册：注册了它，设置页的灰度开关
/// 仍然能写进设置项、也仍然会显示为「已开启」；不注册的话设置项写进去
/// 但界面完全没反应，用户更困惑。
/// </para>
///
/// <para>
/// 将来若要真正实现：可在 Avalonia 侧用 <c>OpacityMask</c> + 一张灰度
/// <c>DrawingBrush</c> 叠加，或在 Shell 层做「按灰度算色」的
/// <c>GrayColorPlanner</c>（Core 已有）并逐个改 token 值。
/// </para>
/// </summary>
public sealed class AvaloniaGrayModeApplier : IGrayModeApplier
{
    /// <summary>当前是否已启用（Avalonia 侧只记录，不真正脱色）。</summary>
    public bool Enabled { get; private set; }

    /// <summary>开启或关闭灰度模式（当前只记录状态）。</summary>
    public void SetEnabled(bool enabled) => Enabled = enabled;
}

/// <summary>
/// Avalonia 侧的音频输出后端。
///
/// <para>
/// **当前是空实现（明确降级）**：WPF 侧用 NAudio；Avalonia 侧还没有接音频库。
/// 曲库浏览、播放状态机都在 Core（可跨框架），缺的只是「把 PCM 送出去」这一步。
/// </para>
///
/// <para>
/// 与灰度一样**显式**注册一个空实现：设置页与音乐页仍可打开、仍能显示曲库，
/// 只是点了播放不出声 —— 比「音乐页整个打不开」对用户更友好。
/// </para>
/// </summary>
public sealed class AvaloniaMusicOutput : IMusicOutput
{
    /// <summary>底层状态机（供 UI 订阅状态变化）。</summary>
    public UMango.Core.Media.Music.MusicPlaybackController Controller { get; }

    /// <summary>音乐库。</summary>
    public UMango.Core.Media.Music.MusicLibrary Library { get; }

    /// <summary>当前是否真的有音频在输出（Avalonia 侧恒为 false）。</summary>
    public bool IsPlaying => false;

    /// <summary>创建后端。</summary>
    public AvaloniaMusicOutput(
        UMango.Core.Media.Music.MusicLibrary library,
        UMango.Core.Media.Music.MusicPlaybackController controller)
    {
        Library = library;
        Controller = controller;
    }

    /// <summary>打开指定曲目（Avalonia 侧尚未接音频库 —— 不做任何事）。</summary>
    public void PlayFile(string path)
    {
        // 待接音频库后在此真正播放。
    }

    /// <summary>停止输出。</summary>
    public void StopOutput()
    {
        // 同上。
    }

    /// <summary>暂停。</summary>
    public void Pause()
    {
        // 同上。
    }

    /// <summary>恢复。</summary>
    public void Resume()
    {
        // 同上。
    }

    /// <summary>
    /// 取当前播放进度（0~1）。
    ///
    /// <para>
    /// Avalonia 侧没有音频输出 → 恒为 0。
    /// </para>
    /// </summary>
    public double GetProgress() => 0d;
}
