using System.Formats.Tar;
using System.IO.Compression;
using UMango.Core.IO;
using Xunit;

namespace UMango.Core.Tests.IO;

/// <summary>
/// 压缩包解压（<c>Files.ExtractFileAsync</c>）的测试。
///
/// 重点覆盖 tar 分支：UMango 的实现改为「**两遍读取**」
/// （第一遍统计总大小算进度，第二遍才真正落盘）。
/// 若退回单遍读取，第二遍拿不到 <c>DataStream</c>，
/// 产出的文件会是 **0 字节** —— 这正是修复前的真实缺陷。
/// </summary>
public sealed class ArchiveExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "umango-archive-" + Guid.NewGuid().ToString("N")[..10]);

    public ArchiveExtractionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* 清理失败无所谓 */ }
    }

    private string MakeTar(string name, params (string Path, string Content)[] entries)
    {
        var tarPath = Path.Combine(_root, name);
        using var fs = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (path, content) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(bytes),
            };
            writer.WriteEntry(entry);
        }
        return tarPath;
    }

    // ────────────────────────────── tar ──────────────────────────────

    [Fact]
    public async Task 解压tar_文件内容完整()
    {
        var tar = MakeTar("a.tar", ("hello.txt", "你好世界"), ("b.bin", "0123456789"));
        var dest = Path.Combine(_root, "out-tar");

        await Files.ExtractFileAsync(tar, dest);

        Assert.Equal("你好世界", await File.ReadAllTextAsync(Path.Combine(dest, "hello.txt")));
        Assert.Equal("0123456789", await File.ReadAllTextAsync(Path.Combine(dest, "b.bin")));
    }

    [Fact]
    public async Task 解压tar_文件不是空的()
    {
        // 回归：单遍读取的实现会写出 0 字节文件
        var tar = MakeTar("b.tar", ("data.txt", "有内容"));
        var dest = Path.Combine(_root, "out-tar2");

        await Files.ExtractFileAsync(tar, dest);

        var info = new FileInfo(Path.Combine(dest, "data.txt"));
        Assert.True(info.Exists);
        Assert.True(info.Length > 0, "解压出的文件不应是空的");
    }

    [Fact]
    public async Task 解压tar_保留子目录结构()
    {
        var tar = MakeTar("c.tar", ("sub/dir/f.txt", "x"));
        var dest = Path.Combine(_root, "out-tar3");

        await Files.ExtractFileAsync(tar, dest);

        Assert.True(File.Exists(Path.Combine(dest, "sub", "dir", "f.txt")));
    }

    [Fact]
    public async Task 解压tar_报告进度()
    {
        var tar = MakeTar("d.tar", ("1.txt", "a"), ("2.txt", "b"));
        var dest = Path.Combine(_root, "out-tar4");

        var seen = new List<double>();
        await Files.ExtractFileAsync(tar, dest, seen.Add);

        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen[^1], 6);
    }

    [Fact]
    public async Task 解压targz_文件内容完整()
    {
        var tar = MakeTar("e.tar", ("z.txt", "压缩内容"));
        var tgz = Path.Combine(_root, "e.tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        {
            using var input = File.OpenRead(tar);
            await input.CopyToAsync(gz);
        }

        var dest = Path.Combine(_root, "out-tgz");
        await Files.ExtractFileAsync(tgz, dest);

        Assert.Equal("压缩内容", await File.ReadAllTextAsync(Path.Combine(dest, "z.txt")));
    }

    // ────────────────────────────── zip ──────────────────────────────

    [Fact]
    public async Task 解压gz_保留原始文件名大小写()
    {
        // 回归：原版用 ToLower() 取输出名，"Plain.txt.gz" 会变成 "plain.txt"
        var gz = Path.Combine(_root, "Plain.Text.gz");
        using (var fs = File.Create(gz))
        using (var gzs = new GZipStream(fs, CompressionLevel.Optimal))
        {
            await gzs.WriteAsync("内容"u8.ToArray());
        }

        var dest = Path.Combine(_root, "out-gz-case");
        await Files.ExtractFileAsync(gz, dest);

        // 不能用 File.Exists 断言大小写 —— macOS/Windows 的文件系统大小写不敏感，
        // 两种拼法都指向同一个文件。必须看**目录里的真实条目名**。
        var names = Directory.GetFiles(dest).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "Plain.Text" }, names);
    }

    [Fact]
    public async Task 解压zip_文件内容完整()
    {
        var zipPath = Path.Combine(_root, "f.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("dir/hello.txt");
            await using var s = entry.Open();
            await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes("zip 内容"));
        }

        var dest = Path.Combine(_root, "out-zip");
        await Files.ExtractFileAsync(zipPath, dest);

        Assert.Equal("zip 内容", await File.ReadAllTextAsync(Path.Combine(dest, "dir", "hello.txt")));
    }

    [Fact]
    public async Task 解压zip_挡住ZipSlip路径逃逸()
    {
        var zipPath = Path.Combine(_root, "evil.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../逃逸.txt");
            await using var s = entry.Open();
            await s.WriteAsync("坏"u8.ToArray());
        }

        var dest = Path.Combine(_root, "out-evil");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Files.ExtractFileAsync(zipPath, dest));

        Assert.False(File.Exists(Path.Combine(_root, "逃逸.txt")));
    }

    // ────────────────────────────── gz ──────────────────────────────

    [Fact]
    public async Task 解压gz_去掉后缀写出原名()
    {
        var gz = Path.Combine(_root, "plain.txt.gz");
        using (var fs = File.Create(gz))
        using (var gzs = new GZipStream(fs, CompressionLevel.Optimal))
        {
            await gzs.WriteAsync("gzip 内容"u8.ToArray());
        }

        var dest = Path.Combine(_root, "out-gz");
        await Files.ExtractFileAsync(gz, dest);

        Assert.Equal("gzip 内容", await File.ReadAllTextAsync(Path.Combine(dest, "plain.txt")));
    }

    // ────────────────────────────── 参数与格式 ──────────────────────────────

    [Fact]
    public async Task 解压_源路径为空_抛参数为空()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Files.ExtractFileAsync(null, _root));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Files.ExtractFileAsync("", _root));
    }

    [Fact]
    public async Task 解压_目标路径为空_抛参数为空()
    {
        var tar = MakeTar("g.tar", ("x.txt", "x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Files.ExtractFileAsync(tar, null));
    }

    [Fact]
    public async Task 解压_不支持的格式_抛不支持()
    {
        var rar = Path.Combine(_root, "x.rar");
        await File.WriteAllTextAsync(rar, "x");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Files.ExtractFileAsync(rar, Path.Combine(_root, "out-rar")));
    }

    [Fact]
    public async Task 解压_目标目录不存在_自动创建()
    {
        var tar = MakeTar("h.tar", ("x.txt", "x"));
        var dest = Path.Combine(_root, "不存在的", "深层", "目录");

        await Files.ExtractFileAsync(tar, dest);

        Assert.True(File.Exists(Path.Combine(dest, "x.txt")));
    }
}
