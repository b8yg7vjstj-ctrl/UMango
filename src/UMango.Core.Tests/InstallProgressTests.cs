using UMango.Core.Install;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>进度解析与阶段归类的测试。</summary>
public class InstallProgressTests
{
    [Theory]
    [InlineData("downloading 45%", 0.45)]
    [InlineData("45.5 %", 0.455)]
    [InlineData("Progress: 100%", 1.0)]
    [InlineData("0%", 0.0)]
    public void 能解析百分比(string line, double expected)
    {
        var p = InstallProgressParser.ParsePercent(line);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.Value, 3);
    }

    [Theory]
    [InlineData("added 12 packages in 3s")]
    [InlineData("no progress here")]
    [InlineData("")]
    [InlineData("   ")]
    public void 解析不到就返回null而不是编造(string line)
    {
        Assert.Null(InstallProgressParser.ParsePercent(line));
    }

    [Fact]
    public void 越界的百分比不被采纳()
    {
        // 999% 明显不是进度（可能是内存占用之类的数字），不应被当成进度
        Assert.Null(InstallProgressParser.ParsePercent("999%"));
    }

    [Fact]
    public void 能解析字符进度条()
    {
        var p = InstallProgressParser.ParsePercent("[####      ]");
        Assert.NotNull(p);
        Assert.InRange(p!.Value, 0.3, 0.5);
    }

    [Theory]
    [InlineData("Resolving dependencies...", StepNames.ResolveDependencies)]
    [InlineData("idealTree buildDeps", StepNames.ResolveDependencies)]
    [InlineData("fetching manifest", StepNames.FetchManifest)]
    [InlineData("Downloading bottle", StepNames.Download)]
    [InlineData("正在下载 tarball", StepNames.Download)]
    [InlineData("Pouring wget", StepNames.LinkWrite)]
    [InlineData("added 5 packages", StepNames.LinkWrite)]
    [InlineData("编译源码中", StepNames.Build)]
    public void 能把不同包管理器的措辞归并到统一阶段(string line, string expected)
    {
        Assert.Equal(expected, InstallProgressParser.ClassifyLine(line));
    }

    [Fact]
    public void 认不出的行不被强行归类()
    {
        Assert.Null(InstallProgressParser.ClassifyLine("hello world"));
    }

    [Fact]
    public void 阶段顺序固定且覆盖全部阶段名()
    {
        Assert.Equal(5, StepNames.Ordered.Length);
        Assert.Contains(StepNames.ResolveDependencies, StepNames.Ordered);
        Assert.Contains(StepNames.FetchManifest, StepNames.Ordered);
        Assert.Contains(StepNames.Download, StepNames.Ordered);
        Assert.Contains(StepNames.Build, StepNames.Ordered);
        Assert.Contains(StepNames.LinkWrite, StepNames.Ordered);
    }
}
