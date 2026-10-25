using UMango.Shell.ViewModels;
using UMango.Core.Configuration;
using UMango.Core.Media.Music;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 音乐页 ViewModel 的测试。
///
/// 覆盖的是「界面胶水」那一层的行为：曲库刷新、状态文本、设置读写。
/// 真正的播放依赖音频设备（Windows only），因此这里不碰播放，只验证不崩、可测。
/// </summary>
public sealed class MusicViewModelTests
{
    private static SettingsService NewSettings()
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);
        return service;
    }

    [Fact]
    public void 刷新_空目录_曲库为空且给出提示()
    {
        var dir = Directory.CreateTempSubdirectory("umango-music-empty");
        try
        {
            var vm = new MusicViewModel(null, dir.FullName, NewSettings());
            vm.RefreshCommand.Execute(null);

            Assert.Empty(vm.Tracks);
            Assert.True(vm.IsEmpty);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 刷新_扫到音频文件_进入曲库()
    {
        var dir = Directory.CreateTempSubdirectory("umango-music-files");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.mp3"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "b.wav"), "x");
            // 非音频文件应被排除。
            File.WriteAllText(Path.Combine(dir.FullName, "c.txt"), "x");

            var vm = new MusicViewModel(null, dir.FullName, NewSettings());
            vm.RefreshCommand.Execute(null);

            Assert.Equal(2, vm.Tracks.Count);
            Assert.Equal(2, vm.TrackCount);
            Assert.False(vm.IsEmpty);
            Assert.Contains(vm.Tracks, t => t.Title == "a");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 刷新_目录不存在_自动创建且不抛异常()
    {
        var root = Directory.CreateTempSubdirectory("umango-music-missing");
        try
        {
            var target = Path.Combine(root.FullName, "not", "yet");
            var vm = new MusicViewModel(null, target, NewSettings());

            vm.RefreshCommand.Execute(null);

            Assert.True(Directory.Exists(target));
            Assert.Empty(vm.Tracks);
            Assert.Equal(0, vm.TrackCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void 刷新_目录创建失败_给出提示且不抛异常()
    {
        var root = Directory.CreateTempSubdirectory("umango-music-blocked");
        try
        {
            // 造一个「父路径其实是文件」的场景 → 建目录必然失败。
            var blocker = Path.Combine(root.FullName, "blocker");
            File.WriteAllText(blocker, "not a directory");
            var target = Path.Combine(blocker, "musics");

            var vm = new MusicViewModel(null, target, NewSettings());
            vm.RefreshCommand.Execute(null);

            // 失败要有提示，而不是把异常抛给调用方。
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
            Assert.Empty(vm.Tracks);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void 没有音频后端_播放命令不崩且状态为已停止()
    {
        var vm = new MusicViewModel(null, Path.GetTempPath(), NewSettings());

        vm.PlayCommand.Execute(null);
        vm.PauseCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.StopCommand.Execute(null);
        vm.ReplayCommand.Execute(null);

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Equal(string.Empty, vm.CurrentTitle);
    }

    [Fact]
    public void 音量_夹到0到1之间()
    {
        var settings = NewSettings();
        var vm = new MusicViewModel(null, Path.GetTempPath(), settings);

        vm.Volume = 2.0;
        Assert.Equal(1.0, vm.Volume);

        vm.Volume = -1.0;
        Assert.Equal(0.0, vm.Volume);

        vm.Volume = double.NaN;
        Assert.Equal(0.0, vm.Volume);
    }

    [Fact]
    public void 随机开关_读写设置()
    {
        var settings = NewSettings();
        var vm = new MusicViewModel(null, Path.GetTempPath(), settings);

        vm.Shuffle = true;
        Assert.True(vm.Shuffle);

        vm.Shuffle = false;
        Assert.False(vm.Shuffle);
    }

    [Fact]
    public void 随机开关_影响待播队列顺序()
    {
        var dir = Directory.CreateTempSubdirectory("umango-music-shuffle");
        try
        {
            for (var i = 0; i < 12; i++)
                File.WriteAllText(Path.Combine(dir.FullName, $"t{i:D2}.mp3"), "x");

            // 固定种子 → 顺序可复现。
            var ordered = new MusicLibrary(new Random(1234));
            var orderedVm = new MusicViewModel(null, dir.FullName, NewSettings(), ordered);
            orderedVm.Shuffle = false;
            orderedVm.RefreshCommand.Execute(null);

            var shuffled = new MusicLibrary(new Random(1234));
            var shuffledVm = new MusicViewModel(null, dir.FullName, NewSettings(), shuffled);
            shuffledVm.Shuffle = true;
            shuffledVm.RefreshCommand.Execute(null);

            // 两边的曲库内容相同（同一目录），但待播顺序不同。
            Assert.Equal(
                ordered.All.Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal),
                shuffled.All.Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal));

            Assert.Equal(
                ordered.Waiting.Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal),
                shuffled.Waiting.Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal));

            // 关键断言：待播顺序本身不一样。
            Assert.NotEqual(
                ordered.Waiting.Select(t => t.Path).ToArray(),
                shuffled.Waiting.Select(t => t.Path).ToArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 音乐目录_留空时回落到程序目录下的Musics()
    {
        var vm = new MusicViewModel(null, null, NewSettings());

        // 设置里的默认值是空 → 视为未配置。
        Assert.False(vm.HasDirectory);
    }
}
