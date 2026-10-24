using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 语义化版本号的解析与比较。
///
/// 这里刻意按 **semver.org 规范**写期望值，而不是照抄 PCL 原版的行为：
/// PCL 原版用整串字典序比较预发布标识符，会把 <c>alpha.10</c> 判成小于 <c>alpha.2</c>。
/// </summary>
public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.0.0-beta.1", 1, 0, 0)]
    [InlineData("2.0.0+build.123", 2, 0, 0)]
    public void 解析出三段版本号(string text, int major, int minor, int patch)
    {
        var v = new SemVer(text);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Fact]
    public void 解析出预发布与构建元数据()
    {
        var v = new SemVer("1.0.0-beta.1+build.7");
        Assert.Equal("beta.1", v.PreRelease);
        Assert.Equal("build.7", v.Build);
        Assert.True(v.IsPreRelease);
        // PCL 用的属性名也要能用
        Assert.Equal(v.PreRelease, v.Prerelease);
        Assert.Equal(v.Build, v.BuildMetadata);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void 预发布版按规范排序(string lower, string higher)
    {
        var a = new SemVer(lower);
        var b = new SemVer(higher);
        Assert.True(a < b, $"{lower} 应当小于 {higher}");
        Assert.True(b > a, $"{higher} 应当大于 {lower}");
        Assert.True(a != b);
    }

    [Fact]
    public void 数字标识符按数值比较而不是字典序()
    {
        // "alpha.10" 字典序小于 "alpha.2"，但规范要求按数值比较。
        var a = new SemVer("1.0.0-alpha.2");
        var b = new SemVer("1.0.0-alpha.10");
        Assert.True(a < b, "alpha.2 应当小于 alpha.10");
    }

    [Fact]
    public void 纯数字标识符优先级低于非数字标识符()
    {
        // 规范：1.0.0-1 < 1.0.0-alpha < 1.0.0-alpha.1
        var numeric = new SemVer("1.0.0-1");
        var alpha = new SemVer("1.0.0-alpha");
        var alphaOne = new SemVer("1.0.0-alpha.1");
        Assert.True(numeric < alpha);
        Assert.True(alpha < alphaOne);
    }

    [Fact]
    public void 正式版高于同版本号的预发布版()
    {
        Assert.True(new SemVer("1.0.0-rc.1") < new SemVer("1.0.0"));
    }

    [Fact]
    public void 构建元数据不参与优先级比较()
    {
        var a = new SemVer("1.0.0+a");
        var b = new SemVer("1.0.0+b");
        Assert.Equal(0, a.CompareTo(b));
        // 但相等性判断按 PCL 原版口径包含构建元数据
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 非法版本号解析失败(string? text)
    {
        Assert.Null(SemVer.TryParse(text!));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("2.0.0+build.123")]
    public void 合法版本号能解析(string text)
    {
        Assert.NotNull(SemVer.TryParse(text));
    }

    [Fact]
    public void 主版本不同视为不兼容()
    {
        Assert.True(new SemVer("2.0.0").IsMajorBump(new SemVer("1.0.0")));
        Assert.True(new SemVer("1.0.0").IsCompatible(new SemVer("1.4.2")));
    }
}
