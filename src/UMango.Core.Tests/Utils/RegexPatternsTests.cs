using UMango.Core.Utils;

namespace UMango.Core.Tests.Utils;

/// <summary>
/// 正则模式测试。
///
/// 这些模式是 PCL-CE Apache-2.0 层的采用件，验证器族与更新检查都依赖它们，
/// 所以单独给一组测试把边界钉住。
/// </summary>
public class RegexPatternsTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/path?q=1&r=2")]
    [InlineData("https://example.com:8443/path")]
    [InlineData("https://127.0.0.1:8080/x")]
    public void 简单网址正则接受合法地址(string url)
        => Assert.Matches(RegexPatterns.HttpUri, url);

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    [InlineData("https://")]
    [InlineData("https://example.com/ with space")]
    public void 简单网址正则拒绝非法地址(string url)
        => Assert.DoesNotMatch(RegexPatterns.HttpUri, url);

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://192.168.1.1/path")]
    [InlineData("https://[::1]/path")]
    public void 完整网址正则接受合法地址(string url)
        => Assert.Matches(RegexPatterns.FullHttpUri, url);

    [Theory]
    [InlineData("https://999.999.999.999")]
    [InlineData("not-a-url")]
    public void 完整网址正则拒绝非法地址(string url)
        => Assert.DoesNotMatch(RegexPatterns.FullHttpUri, url);

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\folder\file.txt")]
    public void unc路径正则接受合法路径(string path)
        => Assert.Matches(RegexPatterns.UncPath, path);

    [Theory]
    [InlineData(@"\\server")]
    [InlineData("C:\\folder")]
    [InlineData("/unix/path")]
    public void unc路径正则拒绝非unc路径(string path)
        => Assert.DoesNotMatch(RegexPatterns.UncPath, path);

    [Theory]
    [InlineData("PROGRA~1")]
    [InlineData("RUNNI~2")]
    public void 短文件名正则命中ntfs短名(string name)
        => Assert.Matches(RegexPatterns.Ntfs83FileName, name);

    [Theory]
    [InlineData("normal.txt")]
    [InlineData("a~1")]
    public void 短文件名正则不命中普通名(string name)
        => Assert.DoesNotMatch(RegexPatterns.Ntfs83FileName, name);

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0+build.5")]
    public void 版本号正则接受标准版本(string version)
        => Assert.Matches(RegexPatterns.SemVer, version);

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("not-a-version")]
    [InlineData("1.0.0.0")]
    public void 版本号正则拒绝非标准版本(string version)
        => Assert.DoesNotMatch(RegexPatterns.SemVer, version);

    [Fact]
    public void 中文字符正则命中中文()
    {
        Assert.Matches(RegexPatterns.HasChineseChar, "中文");
        Assert.DoesNotMatch(RegexPatterns.HasChineseChar, "abc");
    }

    [Fact]
    public void 换行正则覆盖三种换行()
    {
        Assert.Matches(RegexPatterns.NewLine, "a\r\nb");
        Assert.Matches(RegexPatterns.NewLine, "a\nb");
        Assert.Matches(RegexPatterns.NewLine, "a\rb");
    }
}
