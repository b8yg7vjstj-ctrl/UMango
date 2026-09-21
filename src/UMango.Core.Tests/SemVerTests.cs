using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

public class SemVerTests
{
    [Fact]
    public void 解析版本号()
    {
        var v = new SemVer("1.2.3");
        Assert.Equal(1, v.Major); Assert.Equal(2, v.Minor); Assert.Equal(3, v.Patch);
    }

    [Fact]
    public void 预发布版本()
    {
        var v = new SemVer("1.0.0-beta.1");
        Assert.True(v.IsPreRelease);
        Assert.False(new SemVer("1.0.0").IsPreRelease);
    }

    [Fact]
    public void 比较优先级()
    {
        Assert.True(new SemVer("2.0.0") > new SemVer("1.9.9"));
        Assert.True(new SemVer("1.1.0") > new SemVer("1.0.9"));
        Assert.True(new SemVer("1.0.1") > new SemVer("1.0.0"));
    }

    [Fact]
    public void 预发布低于正式版()
    {
        Assert.True(new SemVer("1.0.0") > new SemVer("1.0.0-beta.1"));
    }

    [Fact]
    public void TryParse_合法版本() => Assert.NotNull(SemVer.TryParse("1.2.3"));
    [Fact]
    public void TryParse_非法版本() => Assert.Null(SemVer.TryParse("not-a-version"));
}
