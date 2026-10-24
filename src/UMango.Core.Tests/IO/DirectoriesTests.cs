using System.Text;
using UMango.Core.IO;
using Xunit;

namespace UMango.Core.Tests.IO;

/// <summary>
/// 目录工具（<c>UMango.Core.IO.Directories</c>）的测试。
///
/// 移植自 PCL-CE 的 <c>PCL.Core/IO/Directories.cs</c>（Apache-2.0），
/// 但原版的权限判定是 **Windows-only**（<c>GetAccessControl</c> +
/// <c>WindowsIdentity</c>），在 net10.0 支根本编译不过。
/// UMango 改用「真去写一个临时文件」来判定权限 —— 跨平台且更贴近真实可用性。
/// </summary>
public sealed class DirectoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "umango-directories-" + Guid.NewGuid().ToString("N")[..10]);

    public DirectoriesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* 清理失败无所谓 */ }
    }

    // ────────────────────────────── 权限检查 ──────────────────────────────

    [Fact]
    public async Task 权限检查_普通可写目录_返回真()
    {
        Assert.True(await Directories.CheckPermissionAsync(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 权限检查_路径为空_返回假(string? path)
    {
        Assert.False(await Directories.CheckPermissionAsync(path));
    }

    [Fact]
    public async Task 权限检查_目录不存在_返回假()
    {
        Assert.False(await Directories.CheckPermissionAsync(
            Path.Combine(_root, "不存在的目录")));
    }

    [Fact]
    public async Task 权限检查_不会在目录里留下探针文件()
    {
        Assert.True(await Directories.CheckPermissionAsync(_root));
        // 探针必须被清理掉，否则每次检查都会往用户的目录里丢垃圾
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task 权限检查_只读目录_返回假()
    {
        // 用 chmod 造一个不可写目录（Windows 上跳过 —— ACL 语义不同）
        if (OperatingSystem.IsWindows()) return;

        var readOnly = Path.Combine(_root, "只读");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(await Directories.CheckPermissionAsync(readOnly));
        }
        finally
        {
            File.SetUnixFileMode(readOnly,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ─────────────────────────── 受保护目录判定 ───────────────────────────

    [Fact]
    public void 受保护目录_文件系统根目录_返回真()
    {
        Assert.True(Directories.IsSystemProtectedFolder(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void 受保护目录_Windows回收站与卷信息_返回真()
    {
        Assert.True(Directories.IsSystemProtectedFolder(@"D:\System Volume Information"));
        Assert.True(Directories.IsSystemProtectedFolder(@"D:\$RECYCLE.BIN"));
    }

    [Fact]
    public void 受保护目录_普通目录_返回假()
    {
        Assert.False(Directories.IsSystemProtectedFolder(_root));
    }

    [Fact]
    public async Task 权限检查_受保护目录_返回假()
    {
        Assert.False(await Directories.CheckPermissionAsync(Path.GetPathRoot(_root)!));
    }

    // ─────────────────────────── 抛异常的权限检查 ───────────────────────────

    [Fact]
    public async Task 抛异常版_目录不存在_抛目录不存在()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            Directories.CheckPermissionWithExceptionAsync(Path.Combine(_root, "不存在")));
    }

    [Fact]
    public async Task 抛异常版_路径为空_抛参数为空()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Directories.CheckPermissionWithExceptionAsync("  "));
    }

    [Fact]
    public async Task 抛异常版_受保护目录_抛无权限()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Directories.CheckPermissionWithExceptionAsync(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public async Task 抛异常版_正常目录_不抛()
    {
        await Directories.CheckPermissionWithExceptionAsync(_root);
    }

    // ────────────────────────────── 递归删除 ──────────────────────────────

    [Fact]
    public async Task 递归删除_删掉所有子文件()
    {
        var target = Path.Combine(_root, "待删");
        Directory.CreateDirectory(Path.Combine(target, "a", "b"));
        await File.WriteAllTextAsync(Path.Combine(target, "a", "b", "x.txt"), "内容");
        await File.WriteAllTextAsync(Path.Combine(target, "根.txt"), "内容");

        var deleted = await Directories.DeleteDirectoryAsync(target);

        Assert.True(deleted >= 2);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task 递归删除_目录不存在_返回零且不抛()
    {
        Assert.Equal(0, await Directories.DeleteDirectoryAsync(Path.Combine(_root, "不存在")));
    }

    [Fact]
    public async Task 递归删除_路径为空_返回零()
    {
        Assert.Equal(0, await Directories.DeleteDirectoryAsync(null));
        Assert.Equal(0, await Directories.DeleteDirectoryAsync(""));
    }

    [Fact]
    public async Task 递归删除_不影响同级目录()
    {
        var a = Path.Combine(_root, "A");
        var b = Path.Combine(_root, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        await File.WriteAllTextAsync(Path.Combine(b, "保留.txt"), "x");

        await Directories.DeleteDirectoryAsync(a);

        Assert.False(Directory.Exists(a));
        Assert.True(File.Exists(Path.Combine(b, "保留.txt")));
    }

    // ────────────────────────────── 递归复制 ──────────────────────────────

    [Fact]
    public async Task 递归复制_保留目录结构与内容()
    {
        var from = Path.Combine(_root, "源");
        Directory.CreateDirectory(Path.Combine(from, "子"));
        await File.WriteAllTextAsync(Path.Combine(from, "根.txt"), "根");
        await File.WriteAllTextAsync(Path.Combine(from, "子", "里.txt"), "里");

        var to = Path.Combine(_root, "目标");
        await Directories.CopyDirectoryAsync(from, to);

        Assert.Equal("根", await File.ReadAllTextAsync(Path.Combine(to, "根.txt")));
        Assert.Equal("里", await File.ReadAllTextAsync(Path.Combine(to, "子", "里.txt")));
    }

    [Fact]
    public async Task 递归复制_源路径为空_抛参数为空()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Directories.CopyDirectoryAsync(null, _root));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Directories.CopyDirectoryAsync(_root, ""));
    }

    [Fact]
    public async Task 递归复制_目标不存在_自动创建()
    {
        var from = Path.Combine(_root, "源2");
        Directory.CreateDirectory(from);
        await File.WriteAllTextAsync(Path.Combine(from, "f.txt"), "x");

        var to = Path.Combine(_root, "深", "层", "目标");
        await Directories.CopyDirectoryAsync(from, to);

        Assert.True(File.Exists(Path.Combine(to, "f.txt")));
    }

    [Fact]
    public async Task 递归复制_报告进度且最终为一()
    {
        var from = Path.Combine(_root, "源3");
        Directory.CreateDirectory(from);
        for (var i = 0; i < 3; i++)
            await File.WriteAllTextAsync(Path.Combine(from, $"f{i}.txt"), "x");

        var seen = new List<double>();
        await Directories.CopyDirectoryAsync(from, Path.Combine(_root, "目标3"), seen.Add);

        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen[^1], 6);
    }

    [Fact]
    public async Task 递归复制_带尾部分隔符的源路径_不会切错相对路径()
    {
        // 原版用 file.FullName[fromPath.Length..] 切相对路径，
        // 源路径末尾带分隔符时会多切/少切一个字符。
        var from = Path.Combine(_root, "源4");
        Directory.CreateDirectory(from);
        await File.WriteAllTextAsync(Path.Combine(from, "a.txt"), "x");

        var to = Path.Combine(_root, "目标4");
        await Directories.CopyDirectoryAsync(from + Path.DirectorySeparatorChar, to);

        Assert.True(File.Exists(Path.Combine(to, "a.txt")));
    }

    // ────────────────────────────── 遍历文件 ──────────────────────────────

    [Fact]
    public async Task 遍历文件_含子目录()
    {
        Directory.CreateDirectory(Path.Combine(_root, "t", "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "t", "1.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "t", "sub", "2.txt"), "x");

        var files = await Directories.EnumerateFilesAsync(Path.Combine(_root, "t"));

        Assert.Equal(2, files.Count());
    }

    [Fact]
    public async Task 遍历文件_目录不存在_抛目录不存在()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            Directories.EnumerateFilesAsync(Path.Combine(_root, "不存在")));
    }

    [Fact]
    public async Task 遍历文件_路径为空_抛目录不存在()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            Directories.EnumerateFilesAsync(""));
    }
}
