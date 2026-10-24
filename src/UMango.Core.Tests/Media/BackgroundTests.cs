using UMango.Core.Configuration;
using UMango.Core.Media.Background;
using Xunit;

namespace UMango.Core.Tests.Media;

/// <summary>
/// 动态背景 Core 层的测试：内容库扫描/分类 + 显示策略 + 失败分类。
/// </summary>
public sealed class BackgroundLibraryTests
{
    [Fact]
    public void 扫描_空目录_返回空()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-empty");
        try
        {
            var library = new BackgroundLibrary();
            Assert.Equal(0, library.Reload(dir.FullName));
            Assert.True(library.IsEmpty);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_目录不存在_返回空且不抛异常()
    {
        var library = new BackgroundLibrary();
        Assert.Equal(0, library.Reload(Path.Combine(Path.GetTempPath(), "umango-bg-not-exist-xyz")));
        Assert.True(library.IsEmpty);
    }

    [Fact]
    public void 扫描_图片与视频分到两个清单()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-mix");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.png"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "b.jpg"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "c.mp4"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "d.webm"), "x");

            var library = new BackgroundLibrary();
            var total = library.Reload(dir.FullName);

            Assert.Equal(4, total);
            Assert.Equal(2, library.Images.Count);
            Assert.Equal(2, library.Videos.Count);
            Assert.Contains(library.Images, i => i.Title == "a");
            Assert.Contains(library.Videos, v => v.Title == "c");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_排除ini与db()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-excl");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.png"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "b.ini"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "c.db"), "x");

            var library = new BackgroundLibrary();
            library.Reload(dir.FullName);

            Assert.Single(library.Images);
            Assert.Empty(library.Videos);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_大小写不敏感地识别扩展名()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-case");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.PNG"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "b.MP4"), "x");

            var library = new BackgroundLibrary();
            library.Reload(dir.FullName);

            Assert.Single(library.Images);
            Assert.Single(library.Videos);
            Assert.Equal(".png", library.Images[0].Extension);
            Assert.Equal(".mp4", library.Videos[0].Extension);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_未知扩展名既不算图也不算视频()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-unk");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.xyz"), "x");

            var library = new BackgroundLibrary();
            Assert.Equal(0, library.Reload(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_递归子目录()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-rec");
        try
        {
            var sub = Path.Combine(dir.FullName, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a.png"), "x");

            var library = new BackgroundLibrary();
            library.Reload(dir.FullName);

            Assert.Single(library.Images);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 扫描_按路径排序_保证顺序稳定()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-sort");
        try
        {
            foreach (var name in new[] { "z.png", "a.png", "m.png" })
                File.WriteAllText(Path.Combine(dir.FullName, name), "x");

            var library = new BackgroundLibrary();
            library.Reload(dir.FullName);

            Assert.Equal(
                new[] { "a", "m", "z" },
                library.Images.Select(i => i.Title).ToArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void 重新扫描_清掉上一次的清单()
    {
        var dir = Directory.CreateTempSubdirectory("umango-bg-reload");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.png"), "x");
            var library = new BackgroundLibrary();
            library.Reload(dir.FullName);
            Assert.Single(library.Images);

            File.Delete(Path.Combine(dir.FullName, "a.png"));
            library.Reload(dir.FullName);
            Assert.Empty(library.Images);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

/// <summary>动态背景显示策略的测试。</summary>
public sealed class BackgroundPlaybackPolicyTests
{
    private static BackgroundEnvironment Env(
        bool minimized = false,
        bool busy = false,
        bool forcePlay = false)
        => new() { IsMinimized = minimized, IsBusy = busy, ForcePlay = forcePlay };

    [Fact]
    public void 没有内容_保持停止()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            false, Env(), BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Stopped, state);
    }

    [Fact]
    public void 已停止_不会自己开始()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(), BackgroundPlaybackState.Stopped);

        Assert.Equal(BackgroundPlaybackState.Stopped, state);
    }

    [Fact]
    public void 窗口最小化_暂停()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(minimized: true), BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Paused, state);
    }

    [Fact]
    public void 忙碌且未强制_暂停()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(busy: true), BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Paused, state);
    }

    [Fact]
    public void 忙碌但强制播放_继续播()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(busy: true, forcePlay: true), BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Playing, state);
    }

    [Fact]
    public void 最小化优先于强制播放()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(minimized: true, busy: true, forcePlay: true),
            BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Paused, state);
    }

    [Fact]
    public void 正常情况_保持播放()
    {
        var state = BackgroundPlaybackPolicy.Resolve(
            true, Env(), BackgroundPlaybackState.Playing);

        Assert.Equal(BackgroundPlaybackState.Playing, state);
    }

    [Fact]
    public void 应该播放_无内容为假()
    {
        Assert.False(BackgroundPlaybackPolicy.ShouldPlay(false, Env()));
    }

    [Fact]
    public void 应该播放_最小化为假()
    {
        Assert.False(BackgroundPlaybackPolicy.ShouldPlay(true, Env(minimized: true)));
    }

    [Fact]
    public void 应该播放_忙碌且未强制为假()
    {
        Assert.False(BackgroundPlaybackPolicy.ShouldPlay(true, Env(busy: true)));
    }

    [Fact]
    public void 应该播放_忙碌但强制为真()
    {
        Assert.True(BackgroundPlaybackPolicy.ShouldPlay(true, Env(busy: true, forcePlay: true)));
    }

    [Fact]
    public void 应该播放_正常为真()
    {
        Assert.True(BackgroundPlaybackPolicy.ShouldPlay(true, Env()));
    }
}

/// <summary>动态背景失败分类的测试。</summary>
public sealed class BackgroundFailureClassifierTests
{
    [Fact]
    public void null异常_分类为无失败()
    {
        Assert.Equal(BackgroundFailureKind.None, BackgroundFailureClassifier.Classify(null));
    }

    [Fact]
    public void 文件不存在_分类为文件不存在()
    {
        Assert.Equal(
            BackgroundFailureKind.FileNotFound,
            BackgroundFailureClassifier.Classify(new FileNotFoundException("nope")));
    }

    [Fact]
    public void 目录不存在_分类为文件不存在()
    {
        Assert.Equal(
            BackgroundFailureKind.FileNotFound,
            BackgroundFailureClassifier.Classify(new DirectoryNotFoundException("nope")));
    }

    [Fact]
    public void 含0xC00D109B_分类为非H264()
    {
        var ex = new InvalidOperationException("Media failed. HRESULT 0xC00D109B, An unspecified error occurred.");
        Assert.Equal(BackgroundFailureKind.NotH264, BackgroundFailureClassifier.Classify(ex));
    }

    [Fact]
    public void 含0xC00D109B_大小写不敏感()
    {
        var ex = new InvalidOperationException("0xc00d109b");
        Assert.Equal(BackgroundFailureKind.NotH264, BackgroundFailureClassifier.Classify(ex));
    }

    [Fact]
    public void 格式异常_分类为格式不支持()
    {
        Assert.Equal(
            BackgroundFailureKind.UnsupportedFormat,
            BackgroundFailureClassifier.Classify(new FormatException("bad")));
    }

    [Fact]
    public void 不支持异常_分类为格式不支持()
    {
        Assert.Equal(
            BackgroundFailureKind.UnsupportedFormat,
            BackgroundFailureClassifier.Classify(new NotSupportedException("bad")));
    }

    [Fact]
    public void 其它异常_分类为播放失败()
    {
        Assert.Equal(
            BackgroundFailureKind.PlaybackFailed,
            BackgroundFailureClassifier.Classify(new InvalidOperationException("boom")));
    }
}

/// <summary>背景铺放方式判定的测试。</summary>
public sealed class BackgroundSuitPolicyTests
{
    [Fact]
    public void 智能_图片宽高都小于一半_判为平铺()
    {
        var input = new BackgroundSuitInput(100, 100, 400, 400);
        Assert.Equal(BackgroundSuitMode.Tile, BackgroundSuitPolicy.Resolve(BackgroundSuitMode.Smart, input));
    }

    [Fact]
    public void 智能_图片宽大于一半_判为适应()
    {
        var input = new BackgroundSuitInput(300, 100, 400, 400);
        Assert.Equal(BackgroundSuitMode.Fit, BackgroundSuitPolicy.Resolve(BackgroundSuitMode.Smart, input));
    }

    [Fact]
    public void 智能_图片高大于一半_判为适应()
    {
        var input = new BackgroundSuitInput(100, 300, 400, 400);
        Assert.Equal(BackgroundSuitMode.Fit, BackgroundSuitPolicy.Resolve(BackgroundSuitMode.Smart, input));
    }

    [Fact]
    public void 智能_恰好等于一半_判为适应()
    {
        // PCL 用的是严格小于：等于一半不算"更小" → 适应。
        var input = new BackgroundSuitInput(200, 200, 400, 400);
        Assert.Equal(BackgroundSuitMode.Fit, BackgroundSuitPolicy.Resolve(BackgroundSuitMode.Smart, input));
    }

    [Theory]
    [InlineData(BackgroundSuitMode.Center)]
    [InlineData(BackgroundSuitMode.Fit)]
    [InlineData(BackgroundSuitMode.Stretch)]
    [InlineData(BackgroundSuitMode.Tile)]
    [InlineData(BackgroundSuitMode.TopLeft)]
    [InlineData(BackgroundSuitMode.TopRight)]
    [InlineData(BackgroundSuitMode.BottomLeft)]
    [InlineData(BackgroundSuitMode.BottomRight)]
    public void 非智能模式_原样返回(BackgroundSuitMode mode)
    {
        var input = new BackgroundSuitInput(10, 10, 400, 400);
        Assert.Equal(mode, BackgroundSuitPolicy.Resolve(mode, input));
    }

    [Fact]
    public void 原始尺寸_居中平铺四角为真()
    {
        Assert.True(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.Center));
        Assert.True(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.Tile));
        Assert.True(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.TopLeft));
        Assert.True(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.BottomRight));
    }

    [Fact]
    public void 原始尺寸_适应拉伸为假()
    {
        Assert.False(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.Fit));
        Assert.False(BackgroundSuitPolicy.UsesOriginalSize(BackgroundSuitMode.Stretch));
    }

    [Fact]
    public void 铺满容器_适应拉伸平铺为真()
    {
        Assert.True(BackgroundSuitPolicy.FillsContainer(BackgroundSuitMode.Fit));
        Assert.True(BackgroundSuitPolicy.FillsContainer(BackgroundSuitMode.Stretch));
        Assert.True(BackgroundSuitPolicy.FillsContainer(BackgroundSuitMode.Tile));
    }

    [Fact]
    public void 铺满容器_居中与四角为假()
    {
        Assert.False(BackgroundSuitPolicy.FillsContainer(BackgroundSuitMode.Center));
        Assert.False(BackgroundSuitPolicy.FillsContainer(BackgroundSuitMode.TopLeft));
    }

    [Fact]
    public void 角落对齐_只有四角为真()
    {
        Assert.True(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.TopLeft));
        Assert.True(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.TopRight));
        Assert.True(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.BottomLeft));
        Assert.True(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.BottomRight));
        Assert.False(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.Center));
        Assert.False(BackgroundSuitPolicy.IsCornerAligned(BackgroundSuitMode.Fit));
    }
}

/// <summary>背景设置项的注册与回读测试。</summary>
public sealed class BackgroundSettingsTests
{
    private static SettingsService NewService()
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);
        return service;
    }

    [Fact]
    public void 全部背景键都已注册()
    {
        var service = NewService();
        foreach (var key in new[]
                 {
                     UiSettings.BackgroundEnabledKey,
                     UiSettings.BackgroundDirectoryKey,
                     UiSettings.BackgroundVideoEnabledKey,
                     UiSettings.BackgroundForcePlayKey,
                     UiSettings.BackgroundSuitKey,
                     UiSettings.BackgroundOpacityKey,
                     UiSettings.BackgroundBlurKey,
                 })
        {
            Assert.True(service.Has(key), $"未注册：{key}");
        }
    }

    [Fact]
    public void 铺放方式默认值是智能()
    {
        var service = NewService();
        Assert.Equal(BackgroundSuitMode.Smart, service.Get<BackgroundSuitMode>(UiSettings.BackgroundSuitKey));
    }

    [Fact]
    public void 不透明度默认值是一千()
    {
        var service = NewService();
        Assert.Equal(1000, service.Get<int>(UiSettings.BackgroundOpacityKey));
    }

    [Fact]
    public void 模糊默认值是零()
    {
        var service = NewService();
        Assert.Equal(0, service.Get<int>(UiSettings.BackgroundBlurKey));
    }

    [Fact]
    public void 铺放方式可以写入并读回()
    {
        var service = NewService();
        service.Set(UiSettings.BackgroundSuitKey, BackgroundSuitMode.Tile);
        Assert.Equal(BackgroundSuitMode.Tile, service.Get<BackgroundSuitMode>(UiSettings.BackgroundSuitKey));
    }
}
