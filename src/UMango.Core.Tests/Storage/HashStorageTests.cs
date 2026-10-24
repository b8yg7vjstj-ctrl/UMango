using UMango.Core.Hash;
using UMango.Core.Storage;
using Xunit;

namespace UMango.Core.Tests.Storage;

/// <summary>
/// 内容寻址哈希存储库的测试。
///
/// 其中两条是**回归测试**：它们守的是 PCL 原版里的真实缺陷
/// （见源文件头部注释 3 与 7）。
/// </summary>
public sealed class HashStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly HashStorage _storage;

    public HashStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "umango-hashstore-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_dir);
        _storage = new HashStorage(_dir, new SHA256Provider());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private string WriteTemp(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task 存入后能按哈希取回内容()
    {
        var path = WriteTemp("a.txt", "hello");

        var hash = await _storage.PutAsync(path);

        Assert.NotNull(hash);
        Assert.True(_storage.Exists(hash!));
        using var stream = _storage.Get(hash!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task 存入后文件内容不是空的()
    {
        // 回归测试（PCL 原版缺陷 3）：原版算完哈希后流停在末尾，
        // 随后的 CopyToAsync 拷贝 0 字节 → 库里存出一个空文件。
        var path = WriteTemp("b.txt", "UMango content that must survive");

        var hash = await _storage.PutAsync(path);

        var stored = Path.Combine(_dir, hash![..2], hash!);
        Assert.True(File.Exists(stored));
        Assert.Equal("UMango content that must survive", await File.ReadAllTextAsync(stored));
    }

    [Fact]
    public async Task 重复存入同一个文件不会写两次()
    {
        var path = WriteTemp("c.txt", "same");

        var first = await _storage.PutAsync(path);
        var second = await _storage.PutAsync(path);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task 传入的哈希长度不对时报错()
    {
        // 回归测试（PCL 原版缺陷 7）：原版拿十六进制串长度和**字节数**比，
        // SHA-256 的串长 32、字节数 16 → 永远对不上，调用方传不进 hash。
        var path = WriteTemp("d.txt", "x");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _storage.PutAsync(path, "AB"));
        Assert.Contains("64", ex.Message); // SHA-256 = 32 字节 = 64 个十六进制字符
    }

    [Fact]
    public async Task 传入正确长度的哈希时接受()
    {
        var path = WriteTemp("e.txt", "y");
        var real = await _storage.PutAsync(path);

        var again = await _storage.PutAsync(path, real);

        Assert.Equal(real, again);
    }

    [Fact]
    public async Task 源文件不存在时返回空()
    {
        var hash = await _storage.PutAsync(Path.Combine(_dir, "nope.txt"));
        Assert.Null(hash);
    }

    [Fact]
    public void 不存在的哈希取不到()
    {
        var hash = new string('A', 64);
        Assert.Null(_storage.Get(hash));
        Assert.False(_storage.Exists(hash));
    }

    [Fact]
    public async Task 删除后就没了()
    {
        var path = WriteTemp("f.txt", "delete me");
        var hash = await _storage.PutAsync(path);

        Assert.True(_storage.Delete(hash!));
        Assert.False(_storage.Exists(hash!));
        Assert.False(_storage.Delete(hash!)); // 再删一次返回 false
    }

    [Fact]
    public async Task 压缩模式下也能取回内容()
    {
        var storage = new HashStorage(_dir, new SHA256Provider(), compressObjects: true);
        var path = WriteTemp("g.txt", "compressed content");

        var hash = await storage.PutAsync(path);

        using var stream = storage.Get(hash!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("compressed content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task 放错位置的文件会被挪回正确目录()
    {
        var path = WriteTemp("h.txt", "misplaced");
        var hash = await _storage.PutAsync(path);

        // 模拟早期版本：直接放在库根目录下
        var misplaced = Path.Combine(_dir, hash!);
        var correct = Path.Combine(_dir, hash![..2], hash!);
        File.Move(correct, misplaced, overwrite: true);

        Assert.True(_storage.Exists(hash!)); // 纠正之前也算存在
        using var stream = _storage.Get(hash!);
        Assert.NotNull(stream);
        // 已经被挪回去了
        Assert.True(File.Exists(correct));
        Assert.False(File.Exists(misplaced));
    }

    [Fact]
    public async Task 前缀目录会按哈希前两位分层()
    {
        var path = WriteTemp("i.txt", "layered");
        var hash = await _storage.PutAsync(path);

        Assert.True(File.Exists(Path.Combine(_dir, hash![..2], hash!)));
    }

    [Fact]
    public async Task 不同内容得到不同哈希()
    {
        var a = await _storage.PutAsync(WriteTemp("j1.txt", "one"));
        var b = await _storage.PutAsync(WriteTemp("j2.txt", "two"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task 流式存入也保留完整内容()
    {
        using var source = new MemoryStream("stream payload"u8.ToArray());

        var hash = await _storage.PutAsync(source);

        Assert.NotNull(hash);
        var stored = Path.Combine(_dir, hash![..2], hash!);
        Assert.Equal("stream payload", await File.ReadAllTextAsync(stored));
    }
}
