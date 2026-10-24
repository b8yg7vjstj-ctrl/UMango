using UMango.Core.Hash;
using Xunit;

namespace UMango.Core.Tests.Hash;

/// <summary>
/// 文件哈希缓存的测试。
/// </summary>
public class HashCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public HashCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "umango-hashcache-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "cache.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task 首次计算_会写入缓存()
    {
        var file = WriteFile("a.txt", "hello");
        using var cache = new HashCache(_dbPath);

        var hash = await cache.GetSha256Async(file);

        Assert.Equal(64, hash.Length);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task 第二次请求_命中缓存不重算()
    {
        var file = WriteFile("b.txt", "hello");
        using var cache = new HashCache(_dbPath);

        var first = await cache.GetSha256Async(file);
        var second = await cache.GetSha256Async(file);

        Assert.Equal(first, second);
        // 记录数不应增加（同一路径只有一条）
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task 不同算法_共享同一条记录()
    {
        var file = WriteFile("c.txt", "hello");
        using var cache = new HashCache(_dbPath);

        await cache.GetSha256Async(file);
        await cache.GetMd5Async(file);
        await cache.GetMurmurHash2Async(file);

        // 三种算法写进同一条记录的三个列，而不是三条记录
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task 文件变了_会重算并覆盖旧值()
    {
        var file = WriteFile("d.txt", "hello");
        using var cache = new HashCache(_dbPath);

        var before = await cache.GetSha256Async(file);
        await Task.Delay(20);
        File.WriteAllText(file, "hello world, 这是完全不同的内容");
        var after = await cache.GetSha256Async(file);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task 文件被删_再请求会抛文件不存在且不留残记录()
    {
        var file = WriteFile("e.txt", "hello");
        using var cache = new HashCache(_dbPath);

        await cache.GetSha256Async(file);
        File.Delete(file);

        await Assert.ThrowsAsync<FileNotFoundException>(() => cache.GetSha256Async(file));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task 两个不同缓存实例_互不串扰()
    {
        // 原版 PCL 的 HashCache 把"正在计算的哈希"挂在**静态**字典上，
        // key 不含数据库路径 —— 两个不同 dbPath 的实例对同一文件同一种算法
        // 会共用一次计算，结果只写进先到的那个库，
        // 于是第二个实例的缓存永远不命中。这是 UMango 修掉的真缺陷。
        //
        // 必须**并发**发起：串行发起时前一个计算早已结束、
        // pending 条目已被移除，串扰根本不会发生 —— 那样这条测试是假绿。
        var file = WriteFile("f.txt", "shared content");
        var otherDb = Path.Combine(_dir, "other.db");

        using var first = new HashCache(_dbPath);
        using var second = new HashCache(otherDb);

        var results = await Task.WhenAll(
            first.GetSha256Async(file),
            second.GetSha256Async(file));

        Assert.Equal(results[0], results[1]);
        // 第二个实例自己也必须把结果写进自己的库
        Assert.Equal(1, second.Count);
        Assert.Equal(1, first.Count);
    }

    [Fact]
    public async Task 同一文件并发请求_只算一次()
    {
        var file = WriteFile("g.txt", "concurrent content");
        using var cache = new HashCache(_dbPath);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetSha256Async(file)));

        Assert.Single(results.Distinct());
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task 不支持的算法_明确报错()
    {
        var file = WriteFile("h.txt", "x");
        using var cache = new HashCache(_dbPath);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetHashAsync(file, new FakeProvider()));
    }

    [Fact]
    public async Task 手动移除记录_后会被重算()
    {
        var file = WriteFile("i.txt", "hello");
        using var cache = new HashCache(_dbPath);

        await cache.GetSha256Async(file);
        Assert.True(cache.Remove(file));
        Assert.Equal(0, cache.Count);

        await cache.GetSha256Async(file);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task 清空_会删掉所有记录()
    {
        var file = WriteFile("j.txt", "hello");
        using var cache = new HashCache(_dbPath);
        await cache.GetSha256Async(file);

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    private sealed class FakeProvider : IHashProvider
    {
        public int Length => 1;
        public byte[] ComputeHash(ReadOnlySpan<byte> input) => [0];
        public byte[] ComputeHash(string input, System.Text.Encoding? encoding = null) => [0];
        public byte[] ComputeHash(Stream input) => [0];
        public ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default) => new([0]);
    }
}
