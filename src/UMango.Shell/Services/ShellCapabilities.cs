// SPDX-License-Identifier: Apache-2.0
//
// 平台中立的外壳能力**注册表**。
//
// 为什么要有它：几个 ViewModel（设置页、音乐页、反馈页）需要「宿主能力」
// （剪贴板、音频输出、灰度施加器）。这些能力各壳实现不同，ViewModel 却在 Shell 层。
// 做法是：Shell 提供一个可写的注册表，各壳在启动时把自己那份实现装进去；
// ViewModel 从注册表取（取不到就退化为「该功能不可用」，而不是崩）。
//
// 这与 DI 容器的区别：我们不需要解析、生命周期、作用域 —— 只有几个单例能力。
// 为此拉一个容器进 Core 反而会让 Core 依赖第三方 DI 框架，不值得。

using UMango.Shell.Capabilities;

namespace UMango.Shell.Services;

/// <summary>外壳能力的注册表（进程级单例）。</summary>
public static class ShellCapabilities
{
    /// <summary>剪贴板。取不到时相关功能退化为「无法复制」。</summary>
    public static IClipboardService? Clipboard { get; set; }

    /// <summary>音乐播放后端。取不到时音乐页只保留曲库浏览。</summary>
    public static IMusicOutput? MusicOutput { get; set; }

    /// <summary>灰度模式施加器。取不到时灰度开关只写设置项、不即时生效。</summary>
    public static IGrayModeApplier? GrayModeApplier { get; set; }
}
