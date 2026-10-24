// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐播放状态机（Core 层）单元测试。
//
// 对应实现：
//   src/UMango.Core/Media/Music/MusicPlaybackController.cs
//   src/UMango.Core/Media/Music/MusicLibrary.cs
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Modules/ModMusic.cs`
// （受限许可目录，只借鉴语义、不复制代码）。

using UMango.Core.Media.Music;

namespace UMango.Core.Tests.Media;

public class MusicPlaybackControllerTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-music-ctl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name)
        => File.WriteAllText(Path.Combine(dir, name), "x");

    private static (MusicLibrary Lib, MusicPlaybackController Ctl, string Dir) Build(params string[] names)
    {
        var dir = MakeTempDir();
        foreach (var n in names) WriteFile(dir, n);
        var lib = new MusicLibrary();
        lib.Reload(dir, shuffle: false);
        return (lib, new MusicPlaybackController(lib), dir);
    }

    [Fact]
    public void 初始状态_停止且无当前曲目()
    {
        var lib = new MusicLibrary();
        var ctl = new MusicPlaybackController(lib);

        Assert.Equal(MusicPlaybackState.Stopped, ctl.State);
        Assert.Null(ctl.Current);
        Assert.Equal(0d, ctl.Progress);
    }

    [Fact]
    public void Play_空库_保持停止()
    {
        var lib = new MusicLibrary();
        var ctl = new MusicPlaybackController(lib);

        Assert.Null(ctl.Play(shuffle: false));
        Assert.Equal(MusicPlaybackState.Stopped, ctl.State);
    }

    [Fact]
    public void Play_有曲目_进入播放态并记录当前曲目()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            var track = ctl.Play(shuffle: false);

            Assert.NotNull(track);
            Assert.Equal("a", track!.Title);
            Assert.Equal(MusicPlaybackState.Playing, ctl.State);
            Assert.Same(track, ctl.Current);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Play_不自动播放_进入暂停态()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false, autoplay: false);

            Assert.Equal(MusicPlaybackState.Paused, ctl.State);
            Assert.NotNull(ctl.Current);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Pause_播放态_可暂停()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false);

            Assert.True(ctl.Pause());
            Assert.Equal(MusicPlaybackState.Paused, ctl.State);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Pause_已暂停_返回false()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false, autoplay: false);

            Assert.False(ctl.Pause());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resume_暂停态_可继续()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false, autoplay: false);

            Assert.True(ctl.Resume());
            Assert.Equal(MusicPlaybackState.Playing, ctl.State);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resume_已在播放_返回false()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false);

            Assert.False(ctl.Resume());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resume_无当前曲目_返回false()
    {
        var lib = new MusicLibrary();
        var ctl = new MusicPlaybackController(lib);

        Assert.False(ctl.Resume());
    }

    [Fact]
    public void Stop_清空当前曲目与进度()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false);
            ctl.Progress = 0.5;

            ctl.Stop();

            Assert.Equal(MusicPlaybackState.Stopped, ctl.State);
            Assert.Null(ctl.Current);
            Assert.Equal(0d, ctl.Progress);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReplayCurrent_有当前曲目_重置进度并回到播放态()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false);
            ctl.Progress = 0.8;

            Assert.True(ctl.ReplayCurrent());
            Assert.Equal(0d, ctl.Progress);
            Assert.Equal(MusicPlaybackState.Playing, ctl.State);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReplayCurrent_无当前曲目_返回false()
    {
        var lib = new MusicLibrary();
        var ctl = new MusicPlaybackController(lib);

        Assert.False(ctl.ReplayCurrent());
    }

    [Fact]
    public void PlayNext_推进到下一首()
    {
        var (lib, ctl, dir) = Build("a.mp3", "b.mp3");
        try
        {
            ctl.Play(shuffle: false);
            var next = ctl.PlayNext(shuffle: false);

            Assert.NotNull(next);
            Assert.Equal("b", next!.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 状态变化_触发StateChanged事件()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            var count = 0;
            ctl.StateChanged += () => count++;

            ctl.Play(shuffle: false);
            ctl.Pause();
            ctl.Resume();
            ctl.Stop();

            Assert.Equal(4, count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandleFailure_无异常_什么都不做()
    {
        var (lib, ctl, dir) = Build("a.mp3");
        try
        {
            ctl.Play(shuffle: false);

            Assert.Equal(MusicFailureKind.None, ctl.HandleFailure(null, shuffle: false));
            Assert.Equal(MusicPlaybackState.Playing, ctl.State);
            Assert.NotNull(ctl.Current);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandleFailure_设备被占用_剔除当前曲目并推进下一首()
    {
        var (lib, ctl, dir) = Build("a.mp3", "b.mp3");
        try
        {
            ctl.Play(shuffle: false);

            var kind = ctl.HandleFailure(new InvalidOperationException("AlreadyAllocated"), shuffle: false);

            Assert.Equal(MusicFailureKind.DeviceBusy, kind);
            // a 被剔除
            Assert.DoesNotContain(lib.All, t => t.Title == "a");
            // 自动推进到 b
            Assert.Equal("b", ctl.Current?.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandleFailure_文件不存在_不自动推进()
    {
        var (lib, ctl, dir) = Build("a.mp3", "b.mp3");
        try
        {
            ctl.Play(shuffle: false);

            var kind = ctl.HandleFailure(new FileNotFoundException("x"), shuffle: false);

            Assert.Equal(MusicFailureKind.FileNotFound, kind);
            Assert.Equal(MusicPlaybackState.Stopped, ctl.State);
            Assert.Null(ctl.Current);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HandleFailure_扩展名不支持_归类为格式不支持()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "weird.xyz");
            var lib = new MusicLibrary();
            lib.Reload(dir, shuffle: false);
            var ctl = new MusicPlaybackController(lib);
            ctl.Play(shuffle: false);

            var kind = ctl.HandleFailure(new InvalidOperationException("boom"), shuffle: false);

            Assert.Equal(MusicFailureKind.UnsupportedFormat, kind);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("AlreadyAllocated", MusicFailureKind.DeviceBusy)]
    [InlineData("NoDriver", MusicFailureKind.DeviceChanged)]
    [InlineData("BadDeviceId", MusicFailureKind.DeviceChanged)]
    [InlineData("Got a frame at sample rate", MusicFailureKind.PropertyChangeUnsupported)]
    [InlineData("0xC00D36C4", MusicFailureKind.UnsupportedFormat)]
    [InlineData("随便什么错", MusicFailureKind.PlaybackFailed)]
    public void 失败分类_按消息特征归类(string message, MusicFailureKind expected)
    {
        Assert.Equal(expected, MusicFailureClassifier.Classify(new Exception(message)));
    }

    [Fact]
    public void 失败分类_无异常_返回None()
    {
        Assert.Equal(MusicFailureKind.None, MusicFailureClassifier.Classify(null));
    }

    [Fact]
    public void 失败分类_扩展名不在白名单_归类为格式不支持()
    {
        Assert.Equal(
            MusicFailureKind.UnsupportedFormat,
            MusicFailureClassifier.Classify(new Exception("未知错误"), ".xyz"));
    }

    [Fact]
    public void 失败分类_扩展名在白名单_不因格式归类()
    {
        Assert.Equal(
            MusicFailureKind.PlaybackFailed,
            MusicFailureClassifier.Classify(new Exception("未知错误"), ".mp3"));
    }
}
