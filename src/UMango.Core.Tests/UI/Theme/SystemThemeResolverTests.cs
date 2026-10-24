using UMango.Core.UI.Theme;
using Xunit;

namespace UMango.Core.Tests.UI.Theme;

/// <summary>
/// 系统主题跟随策略的测试。
/// </summary>
public class SystemThemeResolverTests
{
    [Fact]
    public void 跟随系统时采用系统结果()
    {
        Assert.True(SystemThemeResolver.Resolve(ThemeFollowMode.System, SystemThemeState.Known(true), false));
        Assert.False(SystemThemeResolver.Resolve(ThemeFollowMode.System, SystemThemeState.Known(false), true));
    }

    [Fact]
    public void 跟随系统但读不到时用兜底()
    {
        Assert.True(SystemThemeResolver.Resolve(ThemeFollowMode.System, SystemThemeState.Unknown, true));
        Assert.False(SystemThemeResolver.Resolve(ThemeFollowMode.System, SystemThemeState.Unknown, false));
    }

    [Fact]
    public void 强制深色时不看系统()
    {
        Assert.True(SystemThemeResolver.Resolve(ThemeFollowMode.Dark, SystemThemeState.Known(false), false));
    }

    [Fact]
    public void 强制浅色时不看系统()
    {
        Assert.False(SystemThemeResolver.Resolve(ThemeFollowMode.Light, SystemThemeState.Known(true), true));
    }

    [Theory]
    [InlineData("dark", ThemeFollowMode.Dark)]
    [InlineData("Dark", ThemeFollowMode.Dark)]
    [InlineData("light", ThemeFollowMode.Light)]
    [InlineData("system", ThemeFollowMode.System)]
    [InlineData("SYSTEM", ThemeFollowMode.System)]
    [InlineData(null, ThemeFollowMode.System)]
    [InlineData("乱写", ThemeFollowMode.System)]
    public void 设置项里的字符串能正确解析(string? stored, ThemeFollowMode expected)
    {
        Assert.Equal(expected, SystemThemeResolver.FromStorageValue(stored));
    }

    [Fact]
    public void 策略与存储字符串能互相往返()
    {
        foreach (var mode in new[] { ThemeFollowMode.System, ThemeFollowMode.Light, ThemeFollowMode.Dark })
            Assert.Equal(mode, SystemThemeResolver.FromStorageValue(SystemThemeResolver.ToStorageValue(mode)));
    }
}
