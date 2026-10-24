// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐页 ViewModel。
//
// 业务逻辑分两层：
//   · Core 层 `MusicLibrary` / `MusicPlaybackController`（纯逻辑，可单测、跨平台）；
//   · App 层 `NaudioMusicPlayer`（唯一碰音频设备的地方）。
//
// 本类只做「界面 ↔ 上面两层」的胶水：把状态机的状态翻译成界面能绑定的属性，
// 把界面的命令翻译成状态机的调用。
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Modules/ModMusic.cs`
// （受限许可目录，只借鉴语义、不复制代码）。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.App.Localization;
using UMango.App.Media.Music;
using UMango.App.Services;
using UMango.Core.Configuration;
using UMango.Core.Media.Music;

namespace UMango.App.ViewModels;

/// <summary>音乐页 ViewModel：曲库、播放控制、失败提示。</summary>
public partial class MusicViewModel : ObservableObject
{
    private readonly MusicLibrary _library;
    private readonly MusicPlaybackController _controller;
    private readonly NaudioMusicPlayer? _player;
    private readonly string? _directoryOverride;
    private readonly SettingsService? _settings;

    /// <summary>
    /// 构造 ViewModel。
    /// </summary>
    /// <param name="player">
    /// NAudio 播放后端。留 null 表示「没有音频后端」（例如非 Windows 宿主或测试）——
    /// 此时所有播放命令都不做事，只保留曲库浏览能力。
    /// </param>
    /// <param name="directoryOverride">
    /// 音乐目录的**覆盖值**。给 null 时从设置里读。
    /// 留这个口子是因为目录存在设置里，而测试环境没有设置文件。
    /// </param>
    /// <param name="library">
    /// 曲库。留 null 则新建一个（无播放后端时用）。
    /// 留这个口子是为了让「随机播放」的队列顺序可被测试断言。
    /// </param>
    /// <param name="settings">
    /// 设置服务。留 null 表示用全局的 <see cref="AppSettings.Settings"/>。
    /// 留这个口子是因为音量/随机开关存在设置里，而测试环境没有设置文件 ——
    /// 没有它，ViewModel 的测试就只能覆盖「没有设置」这一条路径。
    /// </param>
    public MusicViewModel(
        NaudioMusicPlayer? player = null,
        string? directoryOverride = null,
        SettingsService? settings = null,
        MusicLibrary? library = null)
    {
        _directoryOverride = directoryOverride;
        _settings = settings;
        _library = player?.Library ?? library ?? new MusicLibrary();
        _controller = player?.Controller ?? new MusicPlaybackController(_library);
        _player = player;

        if (_player is not null)
        {
            _controller.StateChanged += OnControllerStateChanged;
        }
    }

    /// <summary>曲库（全部曲目）。</summary>
    public ObservableCollection<MusicTrack> Tracks { get; } = [];

    /// <summary>
    /// 底层曲库。
    ///
    /// 暴露出来是为了让「随机播放」这类**只影响待播队列顺序**的行为可被断言 ——
    /// <see cref="Tracks"/> 是按路径排序的（与随机无关），
    /// 随机只体现在 <c>Library.Waiting</c> 的顺序上。
    /// </summary>
    public MusicLibrary Library => _library;

    /// <summary>状态提示（当前在放什么 / 什么错误）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>实际使用的设置服务（未注入时用全局的）。</summary>
    private SettingsService Settings => _settings ?? AppSettings.Settings;

    /// <summary>音乐目录。</summary>
    public string MusicDirectory => _directoryOverride
        ?? Settings.Get<string>(UiSettings.MusicDirectoryKey);

    /// <summary>是否配置了音乐目录（留空时用程序目录下的 Musics）。</summary>
    public bool HasDirectory => !string.IsNullOrWhiteSpace(MusicDirectory);

    /// <summary>当前曲目名（没在放时为空）。</summary>
    public string CurrentTitle => _controller.Current?.Title ?? "";

    /// <summary>当前状态文本。</summary>
    public string StateText => _controller.State switch
    {
        MusicPlaybackState.Playing => ViewText.L("Music.State.Playing"),
        MusicPlaybackState.Paused => ViewText.L("Music.State.Paused"),
        _ => ViewText.L("Music.State.Stopped"),
    };

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => _controller.State == MusicPlaybackState.Playing;

    /// <summary>是否已暂停。</summary>
    public bool IsPaused => _controller.State == MusicPlaybackState.Paused;

    /// <summary>曲库是否为空。</summary>
    public bool IsEmpty => Tracks.Count == 0;

    /// <summary>
    /// 上次扫描到的曲目数（来自曲库自己的返回值）。
    ///
    /// 单独暴露出来是为了让"状态提示里说的条数"可被测试断言 ——
    /// 它和 <see cref="Tracks"/> 的条数本该一致，但一个是**返回值**一个是**集合**，
    /// 写错时（比如把返回值覆盖成 0）只靠集合长度看不出来。
    /// </summary>
    public int TrackCount { get; private set; }

    /// <summary>音量（0~1）。</summary>
    public double Volume
    {
        get => Settings.Get<double>(UiSettings.MusicVolumeKey);
        set
        {
            var clamped = double.IsNaN(value) ? 0d : Math.Clamp(value, 0d, 1d);
            Settings.Set(UiSettings.MusicVolumeKey, clamped);
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>是否随机播放。</summary>
    public bool Shuffle
    {
        get => Settings.Get<bool>(UiSettings.MusicShuffleKey);
        set
        {
            Settings.Set(UiSettings.MusicShuffleKey, value);
            SaveSettings();
        }
    }

    /// <summary>重新扫描音乐目录并刷新曲库。</summary>
    [RelayCommand]
    public void Refresh()
    {
        var directory = ResolveDirectory();
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            StatusMessage = ViewText.L("Music.DirectoryFailed", ex.Message);
            return;
        }

        var count = _library.Reload(directory, Shuffle);
        TrackCount = count;
        Tracks.Clear();
        foreach (var track in _library.All) Tracks.Add(track);

        OnPropertyChanged(nameof(IsEmpty));
        StatusMessage = count == 0
            ? ViewText.L("Music.NoTracks")
            : ViewText.L("Music.Loaded", count);
    }

    /// <summary>播放（或继续播放）。</summary>
    [RelayCommand]
    public void Play()
    {
        if (_player is null) return;

        // 已经在播放就什么都不做。
        if (_controller.State == MusicPlaybackState.Playing) return;

        // 有当前曲目 → 继续。
        if (_controller.Current is not null)
        {
            _controller.Resume();
            _player.Resume();
            OnControllerStateChanged();
            return;
        }

        // 否则取下一首。
        var track = _controller.Play(Shuffle);
        if (track is null)
        {
            StatusMessage = ViewText.L("Music.NoTracks");
            OnControllerStateChanged();
            return;
        }

        TryPlay(track);
    }

    /// <summary>暂停。</summary>
    [RelayCommand]
    public void Pause()
    {
        if (_player is null) return;
        if (!_controller.Pause()) return;
        _player.Pause();
        OnControllerStateChanged();
    }

    /// <summary>下一首。</summary>
    [RelayCommand]
    public void Next()
    {
        if (_player is null) return;

        var track = _controller.PlayNext(Shuffle);
        if (track is null)
        {
            StatusMessage = ViewText.L("Music.NoTracks");
            OnControllerStateChanged();
            return;
        }

        TryPlay(track);
    }

    /// <summary>停止。</summary>
    [RelayCommand]
    public void Stop()
    {
        _player?.StopOutput();
        _controller.Stop();
        OnControllerStateChanged();
    }

    /// <summary>重播当前曲目。</summary>
    [RelayCommand]
    public void Replay()
    {
        if (_player is null) return;
        if (!_controller.ReplayCurrent()) return;

        if (_controller.Current is not null) TryPlay(_controller.Current);
        OnControllerStateChanged();
    }

    /// <summary>
    /// 真正打开并播放一个曲目；失败时按 PCL 的分类给出提示并推进下一首。
    /// </summary>
    private void TryPlay(MusicTrack track)
    {
        if (_player is null) return;

        try
        {
            _player.PlayFile(track.Path);
            StatusMessage = ViewText.L("Music.NowPlaying", track.Title);
        }
        catch (Exception ex)
        {
            var kind = MusicFailureClassifier.Classify(ex, track.Extension);

            // NAudio 不带 FLAC 解码器 —— 这是"格式不支持"，不是崩溃。
            if (ex is MusicFormatNotSupportedException) kind = MusicFailureKind.UnsupportedFormat;

            StatusMessage = kind switch
            {
                MusicFailureKind.DeviceBusy => ViewText.L("Music.Error.DeviceBusy"),
                MusicFailureKind.DeviceChanged => ViewText.L("Music.Error.DeviceChanged"),
                MusicFailureKind.PropertyChangeUnsupported =>
                    ViewText.L("Music.Error.PropertyChangeUnsupported", track.FileName),
                MusicFailureKind.UnsupportedFormat =>
                    ViewText.L("Music.Error.UnsupportedFormat", track.FileName),
                MusicFailureKind.FileNotFound => ViewText.L("Music.Error.FileNotFound", track.FileName),
                _ => ViewText.L("Music.Error.PlaybackFailed", track.FileName),
            };

            // 播放失败的曲目从库里剔除，并继续下一首（PCL 的行为）。
            _controller.HandleFailure(ex, Shuffle);
        }

        OnControllerStateChanged();
    }

    /// <summary>落盘设置（注入的自有设置服务不落全局磁盘）。</summary>
    private void SaveSettings()
    {
        if (_settings is null) AppSettings.Save();
    }

    /// <summary>解析出实际使用的音乐目录。</summary>
    private string ResolveDirectory()
    {
        var configured = MusicDirectory;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Path.Combine(
            AppContext.BaseDirectory,
            "Musics");
    }

    /// <summary>状态机状态变化 → 刷新界面属性。</summary>
    private void OnControllerStateChanged()
    {
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
    }
}
