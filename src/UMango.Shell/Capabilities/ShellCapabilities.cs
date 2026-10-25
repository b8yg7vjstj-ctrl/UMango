// SPDX-License-Identifier: Apache-2.0
//
// 平台中立的「外壳能力」接口集合。
//
// 为什么要有这一层：几个常用功能在 WPF 侧有现成实现（剪贴板、音频输出、
// 灰度模式、主题切换），在 Avalonia 侧却要另写。把它们抽象成接口后，
// 界面逻辑（ViewModel）就能住在 Shell 层里被两个壳共用，而各壳只负责
// 提供自己那份实现。

using UMango.Core.Media.Music;
using UMango.Shell.Theming;

namespace UMango.Shell.Capabilities;

/// <summary>剪贴板（只放读取文本这一个最小能力）。</summary>
public interface IClipboardService
{
    /// <summary>把文本写入剪贴板。</summary>
    void SetText(string text);
}

/// <summary>
/// 音乐播放后端。
///
/// <para>
/// 平台中立的部分（曲库、播放状态机）已经在 Core 的
/// <c>MusicLibrary</c> / <c>MusicPlaybackController</c> 里；这里只抽象
/// 「真正把声音放出来」的那一步 —— WPF 侧是 NAudio，别的平台可以换实现。
/// </para>
/// </summary>
public interface IMusicOutput
{
    /// <summary>当前是否正在出声。</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// 底层曲库。
    ///
    /// <para>
    /// 暴露出来是为了让「随机播放」这类**只影响待播队列顺序**的行为可被断言 ——
    /// 曲库列表本身是按路径排序的（与随机无关），随机只体现在
    /// <c>Library.Waiting</c> 的顺序上。
    /// </para>
    /// </summary>
    MusicLibrary Library { get; }

    /// <summary>底层播放状态机（供界面订阅状态变化）。</summary>
    MusicPlaybackController Controller { get; }

    /// <summary>播放一个音频文件。</summary>
    void PlayFile(string path);

    /// <summary>停止输出。</summary>
    void StopOutput();

    /// <summary>暂停。</summary>
    void Pause();

    /// <summary>恢复。</summary>
    void Resume();

    /// <summary>当前播放进度（0~1）。</summary>
    double GetProgress();
}

/// <summary>
/// 灰度（哀悼日）模式的施加器。
///
/// <para>
/// PCL-CE 的做法是在 WPF 里把画刷去色（<c>ColorMatrixEffect</c> 之类）；
/// WPF 根本没有 <c>ColorMatrixEffect</c>，UMango 改用「按 Oklab 重新算颜色」
/// 的方式 —— 那是 WPF 专属的实现，抽象在接口后面。
/// </para>
/// </summary>
public interface IGrayModeApplier
{
    /// <summary>开启或关闭灰度模式。</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// 主题切换器（各壳换自己的资源字典）。
/// </summary>
public interface IShellThemeController
{
    /// <summary>切换到指定主题族与深浅色。</summary>
    void Apply(ThemeKind kind, bool dark);

    /// <summary>在当前主题族上切换深浅色。</summary>
    void ToggleDark();
}
