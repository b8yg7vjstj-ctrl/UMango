using UMango.Core.Hash;
using Xunit;

namespace UMango.Core.Tests;

public class HashServiceTests
{
    [Theory]
    [InlineData("hello", "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    public void SHA256_已知值(string text, string expected)
        => Assert.Equal(expected, HashService.ComputeSha256Text(text));

    [Fact]
    public void MD5_一致性()
    {
        var a = HashService.ComputeMd5(System.Text.Encoding.UTF8.GetBytes("test"));
        var b = HashService.ComputeMd5(System.Text.Encoding.UTF8.GetBytes("test"));
        Assert.Equal(a, b);
        Assert.Equal(32, a.Length); // hex string
    }

    [Fact]
    public void SHA256_一致性()
    {
        var a = HashService.ComputeSha256(System.Text.Encoding.UTF8.GetBytes("test"));
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void 空数据()
    {
        var h = HashService.ComputeSha256([]);
        Assert.Equal(64, h.Length);
        Assert.False(string.IsNullOrEmpty(h));
    }
}
