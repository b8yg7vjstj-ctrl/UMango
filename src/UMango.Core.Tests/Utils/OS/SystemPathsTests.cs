using UMango.Core.Utils.OS;
using Xunit;

namespace UMango.Core.Tests.Utils.OS;

/// <summary>系统常用路径。</summary>
public class SystemPathsTests
{
    [Fact]
    public void 根路径非空且是绝对路径()
    {
        var root = SystemPaths.RootPath;
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathRooted(root));
    }

    [Fact]
    public void 系统目录存在()
    {
        Assert.True(Directory.Exists(SystemPaths.SystemDirectory));
    }

    [Fact]
    public void 临时目录存在()
    {
        Assert.True(Directory.Exists(SystemPaths.TempDirectory));
    }

    [Fact]
    public void 非Windows平台的ProgramFiles为空()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(string.Empty, SystemPaths.ProgramFiles);
            Assert.Equal(string.Empty, SystemPaths.ProgramFilesX86);
        }
    }

    [Fact]
    public void 机器名非空()
    {
        Assert.False(string.IsNullOrWhiteSpace(SystemPaths.MachineName));
    }
}
