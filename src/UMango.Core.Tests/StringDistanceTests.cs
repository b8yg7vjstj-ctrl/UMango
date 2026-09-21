using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

public class StringDistanceTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Levenshtein_已知值(string a, string b, int expected)
        => Assert.Equal(expected, StringDistance.Levenshtein(a, b));

    [Theory]
    [InlineData("kitten", "sitting")]
    [InlineData("sitting", "kitten")]
    public void 对称性(string a, string b)
        => Assert.Equal(StringDistance.Levenshtein(a, b), StringDistance.Levenshtein(b, a));

    [Fact]
    public void Similarity_完全匹配()
        => Assert.Equal(1.0, StringDistance.Similarity("hello", "hello"));

    [Fact]
    public void Similarity_完全不匹配()
        => Assert.Equal(0.0, StringDistance.Similarity("abc", "xyz"));

    [Fact]
    public void Similarity_部分匹配()
    {
        var sim = StringDistance.Similarity("kitten", "sitting");
        Assert.InRange(sim, 0.4, 0.7);
    }

    [Fact]
    public void ContainsIgnoreCase_大小写不敏感()
    {
        Assert.True(StringDistance.ContainsIgnoreCase("HelloWorld", "hello"));
        Assert.False(StringDistance.ContainsIgnoreCase("HelloWorld", "xyz"));
    }
}
