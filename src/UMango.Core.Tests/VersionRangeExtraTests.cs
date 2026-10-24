using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 版本范围匹配器的边界情形。
/// </summary>
public class VersionRangeExtraTests
{
    [Theory]
    // 主版本为 0 时 caret 只允许补丁级变动（npm 约定）
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    public void 零主版本的caret范围更严格(string range, string version, bool expected)
        => Assert.Equal(expected, new VersionRange(range).Matches(version));

    [Fact]
    public void 通配符只约束出现通配符之前的段()
    {
        Assert.True(new VersionRange("1.2.x").Matches("1.2.99"));
        Assert.True(new VersionRange("1.2.x").Matches("1.2.0"));
        Assert.False(new VersionRange("1.2.x").Matches("1.3.0"));
        Assert.False(new VersionRange("1.2.x").Matches("2.2.0"));
    }

    [Fact]
    public void 只有一个通配段时只约束主版本()
    {
        Assert.True(new VersionRange("1.x").Matches("1.9.9"));
        Assert.False(new VersionRange("1.x").Matches("2.0.0"));
    }

    [Fact]
    public void 精确匹配不接受补丁级不同的版本()
    {
        Assert.True(new VersionRange("1.2.3").Matches("1.2.3"));
        Assert.False(new VersionRange("1.2.3").Matches("1.2.4"));
    }

    [Fact]
    public void 精确匹配不把构建元数据当成版本差异()
    {
        Assert.True(new VersionRange("1.2.3").Matches("1.2.3+build.9"));
    }

    [Fact]
    public void 大于小于严格生效()
    {
        Assert.False(new VersionRange(">1.0.0").Matches("1.0.0"));
        Assert.True(new VersionRange(">1.0.0").Matches("1.0.1"));
        Assert.True(new VersionRange("<=1.0.0").Matches("1.0.0"));
        Assert.False(new VersionRange("<1.0.0").Matches("1.0.0"));
    }

    [Fact]
    public void 空白范围匹配一切()
    {
        Assert.True(new VersionRange("").Matches("9.9.9"));
        Assert.True(new VersionRange("   ").Matches("9.9.9"));
    }
}
