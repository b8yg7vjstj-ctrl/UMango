// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐：NAudio 播放后端。
//
// 采用 NAudio 2.4.0（**MIT 许可**），与 PCL-CE 原版用的是同一套库、同一许可。
//
// 职责边界：
//   · Core 层的 `MusicLibrary` / `MusicPlaybackController` 是**纯逻辑**（可单测、跨平台）；
//   · 本类是唯一碰音频设备的地方（因此只在 App 层，且只在 Windows 上有意义）。
//
// 格式支持（与 PCL-CE 一致的白名单：.wav / .mp3 / .flac）：
//   · .wav → WaveFileReader
//   · .mp3 → Mp3FileReader
//   · .flac → NAudio 2.x **不含** FLAC 解码器（只有 .NET Framework 专用的
//     NAudio.Flac 1.x 才有，且该包未声明许可）→ 这里会抛
//     `MusicFormatNotSupportedException`，由上层归类为「格式不支持」。
//     这是**如实降级**，不是静默失败。

using NAudio.Wave;
using UMango.Core.Media.Music;

namespace UMango.App.Media.Music;

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

/// <summary>
/// 基于 NAudio 的背景音乐播放器。
///
/// **线程模型**：所有 NAudio 调用都在调用方线程同步完成；
/// NAudio 的 `WaveOutEvent` 自己在后台线程拉数据，不需要额外泵。
/// </summary>
public sealed class NaudioMusicPlayer : IDisposable
{
    private readonly MusicLibrary _library;
    private readonly MusicPlaybackController _controller;
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private bool _disposed;

    /// <summary>创建播放器。</summary>
    public NaudioMusicPlayer(MusicLibrary library, MusicPlaybackController controller)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(controller);
        _library = library;
        _controller = controller;
    }

    /// <summary>底层状态机（供 UI 订阅状态变化）。</summary>
    public MusicPlaybackController Controller => _controller;

    /// <summary>音乐库。</summary>
    public MusicLibrary Library => _library;

    /// <summary>当前是否真的有音频在输出。</summary>
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// 打开指定曲目并开始播放。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <exception cref="MusicFormatNotSupportedException">格式不被支持。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    public void PlayFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(path)) throw new FileNotFoundException("音频文件不存在", path);

        var extension = Path.GetExtension(path).ToLowerInvariant();

        // 先把上一首彻底停下来再开新的（PCL 也是先 Stop 再 Play）。
        StopOutput();

        _reader = CreateReader(path, extension);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.Play();
    }

    /// <summary>
    /// 停止输出并释放音频资源（不改变状态机的 Current）。
    /// </summary>
    public void StopOutput()
    {
        try
        {
            if (_output is not null)
            {
                _output.Stop();
            }
        }
        catch (Exception)
        {
            // 设备已被拔出等情况下 Stop 可能抛异常 —— 忽略，反正资源要丢弃。
        }
        finally
        {
            _output?.Dispose();
            _output = null;

            _reader?.Dispose();
            _reader = null;
        }
    }

    /// <summary>暂停输出。</summary>
    public void Pause()
    {
        if (_output is null) return;
        try
        {
            _output.Pause();
        }
        catch (Exception)
        {
            // 设备已变化：忽略。
        }
    }

    /// <summary>继续输出。</summary>
    public void Resume()
    {
        if (_output is null) return;
        try
        {
            _output.Play();
        }
        catch (Exception)
        {
            // 设备已变化：忽略。
        }
    }

    /// <summary>
    /// 读取当前输出位置，算出播放进度（0~1）。
    /// </summary>
    public double GetProgress()
    {
        if (_reader is null || _reader.Length <= 0) return 0d;
        return Math.Clamp((double)_reader.Position / _reader.Length, 0d, 1d);
    }

    /// <summary>
    /// 按格式创建读取器。
    /// </summary>
    private static WaveStream CreateReader(string path, string extension) => extension switch
    {
        ".wav" => new WaveFileReader(path),
        ".mp3" => new Mp3FileReader(path),
        ".flac" => throw new MusicFormatNotSupportedException(extension),
        _ => throw new MusicFormatNotSupportedException(extension),
    };

    /// <summary>释放音频资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopOutput();
    }
}
