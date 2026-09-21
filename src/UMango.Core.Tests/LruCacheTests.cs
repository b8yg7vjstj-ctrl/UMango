using UMango.Core.Cache;
using Xunit;

namespace UMango.Core.Tests;

public class LruCacheTests
{
    [Fact]
    public void 基本读写() { var c = new LruCache<int, string>(5); c.Set(1, "a"); Assert.True(c.TryGet(1, out var v) && v == "a"); }

    [Fact]
    public void 超出容量淘汰最久未访问的()
    {
        var c = new LruCache<int, int>(3);
        c.Set(1, 1); c.Set(2, 2); c.Set(3, 3); c.Set(4, 4);
        Assert.False(c.TryGet(1, out _)); // 1 被淘汰
        Assert.True(c.TryGet(2, out _));  // 2 仍在
    }

    [Fact]
    public void 访问会刷新顺序()
    {
        var c = new LruCache<int, int>(3);
        c.Set(1, 1); c.Set(2, 2); c.Set(3, 3);
        c.TryGet(1, out _); // 1 被访问，移到最前
        c.Set(4, 4); // 现在 2 应该被淘汰（最久未访问）
        Assert.True(c.TryGet(1, out _)); // 1 仍在
        Assert.False(c.TryGet(2, out _)); // 2 被淘汰
    }

    [Fact]
    public void 删除后不再可获取() { var c = new LruCache<string, int>(5); c.Set("a", 1); Assert.True(c.TryRemove("a")); Assert.False(c.TryGet("a", out _)); }

    [Fact]
    public void 清空后计数归零() { var c = new LruCache<int, int>(5); c.Set(1, 1); c.Clear(); Assert.Equal(0, c.Count); }
}
