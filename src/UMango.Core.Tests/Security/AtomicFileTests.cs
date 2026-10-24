using UMango.Core.Security;
using Xunit;

namespace UMango.Core.Tests.Security;

/// <summary>
/// 原子文件写入的测试。
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "umango-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败可忽略 */ }
    }

    [Fact]
    public void 写入的内容与传入一致()
    {
        var path = Path.Combine(_dir, "a.bin");
        var data = new byte[] { 1, 2, 3, 4 };

        AtomicFile.Write(path, data);

        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void 临时文件写在同一个目录里()
    {
        // 跨目录的移动在 Windows 上可能是复制（非原子），
        // 所以临时文件必须和目标同目录。
        var path = Path.Combine(_dir, "a.bin");
        string? tempPath = null;

        AtomicFile.Write(path, new byte[] { 1 }, p => tempPath = p);

        Assert.NotNull(tempPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(tempPath!)),
                     Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    [Fact]
    public void 临时文件写完时目标文件还是旧内容()
    {
        // 这就是「原子」的可观察形态：
        // 目标文件在替换之前始终是完整的旧内容，不会出现半截状态。
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });

        var targetAtTempTime = new byte[1];
        AtomicFile.Write(path, new byte[] { 1, 2 }, _ => targetAtTempTime[0] = File.ReadAllBytes(path)[0]);

        Assert.Equal(9, targetAtTempTime[0]);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void 临时文件里确实是新内容()
    {
        // 只断言「目标文件没被提前改」是不够的 ——
        // 一个「什么都不写、只调回调」的实现也能骗过上一条。
        // 这条要求回调触发时临时文件里躺着完整的新数据。
        var path = Path.Combine(_dir, "a.bin");
        byte[]? tempContent = null;

        AtomicFile.Write(path, new byte[] { 7, 7, 7, 7 }, tmp => tempContent = File.ReadAllBytes(tmp));

        Assert.NotNull(tempContent);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, tempContent);
    }

    [Fact]
    public void 临时文件在回调时确实存在()
    {
        // 上一条若实现是「先调回调再写文件」就会读不到文件而抛异常；
        // 这里显式断言存在性，把契约写死。
        var path = Path.Combine(_dir, "a.bin");
        var existed = false;

        AtomicFile.Write(path, new byte[] { 1 }, tmp => existed = File.Exists(tmp));

        Assert.True(existed);
    }

    [Fact]
    public void 写完不残留临时文件()
    {
        var path = Path.Combine(_dir, "a.bin");

        AtomicFile.Write(path, new byte[] { 1 });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void 覆盖已有文件()
    {
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[] { 9 });

        AtomicFile.Write(path, new byte[] { 1, 2 });

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
    }
}
