using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

public class VersionRangeTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("^1.2.3", "1.2.5", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("~1.2.3", "1.2.5", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData("<2.0.0", "1.9.9", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    public void 版本范围匹配(string range, string version, bool expected)
        => Assert.Equal(expected, new VersionRange(range).Matches(version));

    [Fact]
    public void 星号匹配所有版本() => Assert.True(new VersionRange("*").Matches("1.2.3"));

    [Fact]
    public void 通配符匹配() => Assert.True(new VersionRange("1.2.x").Matches("1.2.9"));
}
