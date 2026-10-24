// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐（Core 层）单元测试。
//
// 不联网、不碰音频设备 —— 全部是纯逻辑，因此可在 macOS 上直接跑。
//
// 对应实现：
//   src/UMango.Core/Media/Music/MusicLibrary.cs
//   src/UMango.Core/Media/Music/MusicPlaybackController.cs
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Modules/ModMusic.cs`
// （受限许可目录，只借鉴语义、不复制代码）。

using UMango.Core.Media.Music;

namespace UMango.Core.Tests.Media;

public class MusicLibraryTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void 扫描_空目录_返回空列表()
    {
        var dir = MakeTempDir();
        try
        {
            Assert.Empty(MusicLibrary.Scan(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描_目录不存在_返回空列表()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Empty(MusicLibrary.Scan(missing));
    }

    [Fact]
    public void 扫描_排除PCL那串非音频扩展()
    {
        var dir = MakeTempDir();
        try
        {
            foreach (var ext in MusicLibrary.ExcludedExtensions) WriteFile(dir, "a" + ext);
            WriteFile(dir, "song.mp3");

            var tracks = MusicLibrary.Scan(dir);

            Assert.Single(tracks);
            Assert.Equal("song.mp3", tracks[0].FileName);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描_递归子目录()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, Path.Combine("sub", "deep.wav"));

            var tracks = MusicLibrary.Scan(dir);

            Assert.Single(tracks);
            Assert.Equal("deep", tracks[0].Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描_按路径排序_保证非随机模式顺序稳定()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "b.mp3");
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "c.mp3");

            var titles = MusicLibrary.Scan(dir).Select(t => t.Title).ToArray();

            Assert.Equal(["a", "b", "c"], titles);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描_扩展名小写化_且曲名去掉扩展名()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "My Song.MP3");

            var track = Assert.Single(MusicLibrary.Scan(dir));

            Assert.Equal(".mp3", track.Extension);
            Assert.Equal("My Song", track.Title);
            Assert.True(track.IsSupportedFormat);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 扫描_未知扩展名_收下但标记为不支持()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "weird.xyz");

            var track = Assert.Single(MusicLibrary.Scan(dir));

            Assert.False(track.IsSupportedFormat);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Reload_填充全部曲目并重建队列()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.wav");
            var lib = new MusicLibrary();

            var count = lib.Reload(dir, shuffle: false);

            Assert.Equal(2, count);
            Assert.Equal(2, lib.All.Count);
            Assert.Equal(2, lib.Waiting.Count);
            Assert.False(lib.IsEmpty);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 重建队列_随机模式_顺序可复现()
    {
        var dir = MakeTempDir();
        try
        {
            for (var i = 0; i < 8; i++) WriteFile(dir, $"s{i}.mp3");

            var lib1 = new MusicLibrary(new Random(1234));
            lib1.Reload(dir, shuffle: true);
            var first = lib1.Waiting.Select(t => t.Title).ToArray();

            var lib2 = new MusicLibrary(new Random(1234));
            lib2.Reload(dir, shuffle: true);
            var second = lib2.Waiting.Select(t => t.Title).ToArray();

            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 重建队列_随机模式_与原序不同()
    {
        var dir = MakeTempDir();
        try
        {
            for (var i = 0; i < 12; i++) WriteFile(dir, $"s{i}.mp3");

            var lib = new MusicLibrary(new Random(7));
            lib.Reload(dir, shuffle: true);
            var shuffled = lib.Waiting.Select(t => t.Title).ToArray();

            var sorted = MusicLibrary.Scan(dir).Select(t => t.Title).ToArray();

            Assert.NotEqual(sorted, shuffled);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 重建队列_preventFirst_把队首那首挪到队尾()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            WriteFile(dir, "c.mp3");
            var lib = new MusicLibrary();

            lib.Reload(dir, shuffle: false);
            var head = lib.Waiting[0].Path;
            lib.RebuildQueue(shuffle: false, preventFirst: head);

            Assert.Equal(3, lib.Waiting.Count);
            Assert.NotEqual(head, lib.Waiting[0].Path);
            Assert.Equal(head, lib.Waiting[^1].Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void 重建队列_preventFirst_不是队首则不动()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            var lib = new MusicLibrary();

            lib.Reload(dir, shuffle: false);
            var tail = lib.Waiting[1].Path;
            lib.RebuildQueue(shuffle: false, preventFirst: tail);

            Assert.Equal(lib.All[0].Path, lib.Waiting[0].Path);
            Assert.Equal(tail, lib.Waiting[1].Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PeekNext_不改变队列()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            var lib = new MusicLibrary();
            lib.Reload(dir, shuffle: false);

            var peeked = lib.PeekNext(shuffle: false);

            Assert.NotNull(peeked);
            Assert.Equal("a", peeked!.Title);
            // 队列没有被取走
            Assert.Equal(2, lib.Waiting.Count);
            Assert.Equal("a", lib.Waiting[0].Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PeekNext_空库_返回null()
    {
        var lib = new MusicLibrary();
        Assert.Null(lib.PeekNext(shuffle: false));
    }

    [Fact]
    public void DequeueNext_取走队首并移出队列()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            var lib = new MusicLibrary();
            lib.Reload(dir, shuffle: false);

            var next = lib.DequeueNext(shuffle: false);

            Assert.NotNull(next);
            Assert.Equal("a", next!.Title);
            Assert.Single(lib.Waiting);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DequeueNext_队列取空后重建_且刚播完的挪到队尾()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            var lib = new MusicLibrary();
            lib.Reload(dir, shuffle: false);

            var first = lib.DequeueNext(shuffle: false);
            Assert.Equal("a", first!.Title);

            // 取第二首：队列已空 → 触发重建
            var second = lib.DequeueNext(shuffle: false);

            Assert.NotNull(second);
            // 重建后 preventFirst = 刚播完的 a → a 在队尾，b 在队首
            Assert.Equal("b", second!.Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DequeueNext_空库_返回null()
    {
        var lib = new MusicLibrary();
        Assert.Null(lib.DequeueNext(shuffle: false));
    }

    [Fact]
    public void Remove_同时从全部与队列里移除()
    {
        var dir = MakeTempDir();
        try
        {
            WriteFile(dir, "a.mp3");
            WriteFile(dir, "b.mp3");
            var lib = new MusicLibrary();
            lib.Reload(dir, shuffle: false);

            lib.Remove(lib.All[0]);

            Assert.Single(lib.All);
            Assert.Single(lib.Waiting);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
