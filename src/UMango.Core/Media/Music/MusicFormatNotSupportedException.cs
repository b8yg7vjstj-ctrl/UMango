// SPDX-License-Identifier: Apache-2.0
//
// 音频格式不被当前播放后端支持。
//
// 为什么放在 Core：NAudio 2.x **不含** FLAC 解码器（只有 .NET Framework 专用的
// NAudio.Flac 1.x 才有，且该包未声明许可）→ WPF 侧会抛这个异常，由界面归类为
// 「格式不支持」。但「哪个后端支持哪些格式」是**后端**的知识，把异常放在
// Core 里，界面层就不必认识任何具体后端的类型。

namespace UMango.Core.Media.Music;

/// <summary>音频格式不被当前后端支持。</summary>
public sealed class MusicFormatNotSupportedException : Exception
{
    /// <summary>创建异常。</summary>
    public MusicFormatNotSupportedException(string extension)
        : base($"当前音频后端不支持该格式：{extension}")
        => Extension = extension;

    /// <summary>不被支持的扩展名。</summary>
    public string Extension { get; }
}
