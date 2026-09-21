using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests;

public class ExtensionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("hello", false)]
    public void IsBlank(string? s, bool expected) => Assert.Equal(expected, s.IsBlank());

    [Fact]
    public void Truncate_截断并加省略号() => Assert.Equal("hel…", "hello world".Truncate(3));

    [Fact]
    public void Truncate_不截断短字符串() => Assert.Equal("hi", "hi".Truncate(10));

    [Fact]
    public void SafeSubstring_越界不抛() => Assert.Equal("lo", "hello".SafeSubstring(3, 10));

    [Fact]
    public void Between_找到中间内容() => Assert.Equal("world", "hello [world] !".Between("[", "]"));

    [Fact]
    public void Between_找不到返回null() => Assert.Null("hello".Between("[", "]"));

    [Fact]
    public void AfterFirst_返回标记之后的内容() => Assert.Equal(" world", "hello world".AfterFirst("hello"));

    [Fact]
    public void BeforeLast_返回标记之前的内容() => Assert.Equal("hello", "hello world".BeforeLast(" world"));
}
