using UMango.Core.UI.Svg;
using Xunit;

namespace UMango.Core.Tests.UI.Svg;

/// <summary>SVG 图标标识的解析与路径安全校验。</summary>
public class SvgIconKeyTests
{
    [Fact]
    public void 单段名称走默认包()
    {
        var key = SvgIconKey.TryParse("home");
        Assert.NotNull(key);
        Assert.Equal("default", key!.Value.Pack);
        Assert.Equal("home", key.Value.Name);
    }

    [Fact]
    public void 两段名称是包与图标名()
    {
        var key = SvgIconKey.TryParse("material/home");
        Assert.Equal("material", key!.Value.Pack);
        Assert.Equal("home", key.Value.Name);
    }

    [Fact]
    public void 去掉后缀与反斜杠()
    {
        var key = SvgIconKey.TryParse("material\\home.svg");
        Assert.Equal("material", key!.Value.Pack);
        Assert.Equal("home", key.Value.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret")]
    [InlineData("material/../secret")]
    [InlineData("/absolute")]
    [InlineData("material/")]
    public void 非法标识被拒绝(string? icon)
    {
        Assert.Null(SvgIconKey.TryParse(icon));
    }

    [Theory]
    [InlineData("material/home")]
    [InlineData("material/sub-dir/home")]
    public void 合法标识被接受(string icon)
    {
        Assert.NotNull(SvgIconKey.TryParse(icon));
    }
}
