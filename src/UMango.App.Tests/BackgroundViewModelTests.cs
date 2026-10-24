// SPDX-License-Identifier: Apache-2.0
//
// 动态背景 ViewModel 的测试。
//
// 背景判定全在 Core（BackgroundLibrary / BackgroundPlaybackPolicy /
// BackgroundFailureClassifier / BackgroundSuitPolicy），这里只测 ViewModel 的编排：
//   · 目录覆盖值优先于设置值；
//   · 空目录 → 不挑内容、给出提示；
//   · 挑到的内容出现在库里；
//   · 状态机解析把「最小化 / 忙碌 / 强制播放」翻译成正确的播放状态。

using UMango.Core.Configuration;
using UMango.Core.Media.Background;
using UMango.App.ViewModels;
using Xunit;

namespace UMango.App.Tests;

public sealed class BackgroundViewModelTests
{
    private static SettingsService NewService()
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);
        return service;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>造一个「有内容」的背景目录并把 ViewModel 的库填上。</summary>
    private static BackgroundViewModel NewLoadedVm(SettingsService service, out string dir)
    {
        dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "bg.png"), "x");
        var vm = new BackgroundViewModel(service, dir);
        vm.Refresh();
        return vm;
    }

    [Fact]
    public void 目录覆盖值优先于设置值()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundDirectoryKey, "/设置里的目录");

        var vm = new BackgroundViewModel(service, "/覆盖的目录");

        Assert.Equal("/覆盖的目录", vm.Directory);
    }

    [Fact]
    public void 没有覆盖值时读设置()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundDirectoryKey, "/设置里的目录");

        var vm = new BackgroundViewModel(service);

        Assert.Equal("/设置里的目录", vm.Directory);
    }

    [Fact]
    public void 目录留空时回落到程序目录下的Pictures()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundDirectoryKey, "");

        var vm = new BackgroundViewModel(service);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Pictures"), vm.Directory);
    }

    [Fact]
    public void 扫描空目录_不挑内容并给出提示()
    {
        var dir = NewTempDir();
        try
        {
            var vm = new BackgroundViewModel(NewService(), dir);

            var picked = vm.Refresh();

            Assert.Null(picked);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描有内容_挑到的文件在库里()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bg.png"), "x");
            var vm = new BackgroundViewModel(NewService(), dir);

            var picked = vm.Refresh();

            Assert.NotNull(picked);
            Assert.Contains(vm.Library.Images, i => i.Path == picked);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 最小化时状态变为暂停()
    {
        var vm = NewLoadedVm(NewService(), out var dir);
        try
        {
            var state = vm.ResolveState(BackgroundPlaybackState.Playing, isMinimized: true, isBusy: false);

            Assert.Equal(BackgroundPlaybackState.Paused, state);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 忙碌且未强制播放时状态变为暂停()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundForcePlayKey, false);
        var vm = NewLoadedVm(service, out var dir);
        try
        {
            var state = vm.ResolveState(BackgroundPlaybackState.Playing, isMinimized: false, isBusy: true);

            Assert.Equal(BackgroundPlaybackState.Paused, state);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 忙碌但强制播放时继续播放()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundForcePlayKey, true);
        var vm = NewLoadedVm(service, out var dir);
        try
        {
            var state = vm.ResolveState(BackgroundPlaybackState.Playing, isMinimized: false, isBusy: true);

            Assert.Equal(BackgroundPlaybackState.Playing, state);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 没有内容时状态是停止()
    {
        var vm = new BackgroundViewModel(NewService(), NewTempDir());

        var state = vm.ResolveState(BackgroundPlaybackState.Playing, isMinimized: false, isBusy: false);

        Assert.Equal(BackgroundPlaybackState.Stopped, state);
    }

    [Fact]
    public void 记录失败_非H264给出对应提示()
    {
        var vm = new BackgroundViewModel(NewService(), NewTempDir());

        vm.ReportFailure(BackgroundFailureKind.NotH264);

        Assert.Equal(BackgroundFailureKind.NotH264, vm.LastFailure);
        Assert.Contains("H.264", vm.StatusMessage);
    }
}
