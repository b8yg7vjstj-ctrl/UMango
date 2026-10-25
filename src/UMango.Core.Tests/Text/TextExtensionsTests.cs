// SPDX-License-Identifier: Apache-2.0
//
// UMango.Core.Text.TextExtensions 的单元测试。
//
// 这些用例钉住**独立实现与 PCL 原版的行为等价性**，
// 同时覆盖已修正的三处原版缺陷。

using System.Text.RegularExpressions;
using UMango.Core.Text;
using Xunit;

namespace UMango.Core.Tests.Text;

public class TextExtensionsTests
{
    // ================= Capitalize =================

    [Theory]
    [InlineData("hello", "Hello")]
    [InlineData("HELLO", "Hello")]
    [InlineData("hELLO wORLD", "Hello world")]
    [InlineData("", "")]
    public void Capitalize首字母大写其余小写(string input, string expected)
        => Assert.Equal(expected, input.Capitalize());

    // ================= StrFill =================

    [Fact]
    public void StrFill过短则左侧填充()
        => Assert.Equal("007", "7".StrFill("0", 3));

    [Fact]
    public void StrFill过长则左侧截断()
        => Assert.Equal("123", "12345".StrFill("0", 3));

    [Fact]
    public void StrFill长度正好则原样()
        => Assert.Equal("123", "123".StrFill("0", 3));

    [Fact]
    public void StrFill空分隔符不抛异常()
    {
        // 原版在 code 为空串时抛 IndexOutOfRangeException（code[0]）。
        Assert.Equal("  7", "7".StrFill("", 3));
    }

    // ================= StrFillNum =================

    [Fact]
    public void StrFillNum补足小数位()
        => Assert.Equal("12.00", 12d.StrFillNum(2));

    [Fact]
    public void StrFillNum四舍五入()
        => Assert.Equal("95.68", 95.678d.StrFillNum(2));

    // ================= StrTrim =================

    [Fact]
    public void StrTrim去掉首尾标点()
        => Assert.Equal("你好", "你好。".StrTrim());

    [Fact]
    public void StrTrim去掉括号补充说明()
        => Assert.Equal("标题", "标题（说明）".StrTrim());

    [Fact]
    public void StrTrim去掉冒号后内容()
        => Assert.Equal("标题", "标题：补充".StrTrim());

    [Fact]
    public void StrTrim保留括号内容当关闭removeQuote()
        => Assert.Equal("标题（说明）", "标题（说明）".StrTrim(false));

    // ================= Join / Split =================

    [Fact]
    public void Join用分隔符连接()
        => Assert.Equal("a,b,c", new[] { "a", "b", "c" }.Join(","));

    [Fact]
    public void Join空集合得到空串()
        => Assert.Equal("", Array.Empty<string>().Join(","));

    [Fact]
    public void Join跳过null元素()
        => Assert.Equal("a,,c", new string?[] { "a", null, "c" }.Join(","));

    [Fact]
    public void Split单字符分隔()
        => Assert.Equal(new[] { "a", "b" }, "a,b".Split(","));

    [Fact]
    public void Split多字符分隔()
        => Assert.Equal(new[] { "a", "b" }, "a::b".Split("::"));

    // ================= GetHash / IsAscii =================

    [Fact]
    public void GetHash是稳定的()
        => Assert.Equal("hello".GetHash(), "hello".GetHash());

    [Fact]
    public void GetHash不同字符串不同哈希()
        => Assert.NotEqual("hello".GetHash(), "world".GetHash());

    [Fact]
    public void IsAscii判定()
    {
        Assert.True("abc123".IsAscii());
        Assert.False("中文".IsAscii());
    }

    // ================= BeforeFirst / AfterFirst / BeforeLast / AfterLast =================

    [Fact]
    public void BeforeFirst取首次出现之前()
        => Assert.Equal("2024", "2024/11/08".BeforeFirst("/"));

    [Fact]
    public void AfterFirst取首次出现之后()
        => Assert.Equal("11/08", "2024/11/08".AfterFirst("/"));

    [Fact]
    public void BeforeLast取最后出现之前()
        => Assert.Equal("2024/11", "2024/11/08".BeforeLast("/"));

    [Fact]
    public void AfterLast取最后出现之后()
        => Assert.Equal("08", "2024/11/08".AfterLast("/"));

    [Fact]
    public void 未找到时原样返回()
    {
        Assert.Equal("abc", "abc".BeforeFirst("X"));
        Assert.Equal("abc", "abc".AfterFirst("X"));
        Assert.Equal("abc", "abc".BeforeLast("X"));
        Assert.Equal("abc", "abc".AfterLast("X"));
    }

    [Fact]
    public void 空分隔符视为未找到()
        => Assert.Equal("abc", "abc".BeforeFirst(""));

    [Fact]
    public void 忽略大小写模式生效()
    {
        // "aBc" 中的字符是 a-B-c（大写 B），所以两种模式都应命中。
        Assert.Equal("a", "aBc".BeforeFirst("B", ignoreCase: true));
        Assert.Equal("a", "aBc".BeforeFirst("B"));
        // 小写 b 只在忽略大小写时命中。
        Assert.Equal("a", "aBc".BeforeFirst("b", ignoreCase: true));
        Assert.Equal("aBc", "aBc".BeforeFirst("b"));
    }

    // ================= Between =================

    [Fact]
    public void Between取两个分隔符之间()
        => Assert.Equal("b", "a[b]c".Between("[", "]"));

    [Fact]
    public void Between的after取最后一次出现()
        => Assert.Equal("X", "[a][X]".Between("[", "]"));

    [Fact]
    public void Between找不到before时退化为截到末尾()
        => Assert.Equal("bc", "a[bc".Between("[", "]"));

    [Fact]
    public void Between完全找不到时原样返回()
        => Assert.Equal("abc", "abc".Between("[", "]"));

    // ================= 高速比较 =================

    [Fact]
    public void StartsWithF与StartsWith一致()
    {
        Assert.True("abc".StartsWithF("ab"));
        Assert.False("abc".StartsWithF("AB"));
        Assert.True("abc".StartsWithF("AB", ignoreCase: true));
    }

    [Fact]
    public void EndsWithF与EndsWith一致()
    {
        Assert.True("abc".EndsWithF("bc"));
        Assert.False("abc".EndsWithF("BC"));
        Assert.True("abc".EndsWithF("BC", ignoreCase: true));
    }

    [Fact]
    public void ContainsF与Contains一致()
    {
        Assert.True("abc".ContainsF("b"));
        Assert.False("abc".ContainsF("B"));
        Assert.True("abc".ContainsF("B", ignoreCase: true));
    }

    [Fact]
    public void IndexOfF返回位置或负一()
    {
        Assert.Equal(1, "abc".IndexOfF("b"));
        Assert.Equal(-1, "abc".IndexOfF("B"));
        Assert.Equal(1, "abc".IndexOfF("B", ignoreCase: true));
    }

    [Fact]
    public void IndexOfF带起始位置()
        => Assert.Equal(3, "abcbc".IndexOfF("b", 2));

    [Fact]
    public void LastIndexOfF返回位置或负一()
    {
        Assert.Equal(3, "abcbc".LastIndexOfF("b"));
        Assert.Equal(-1, "abcbc".LastIndexOfF("B"));
        Assert.Equal(3, "abcbc".LastIndexOfF("B", ignoreCase: true));
    }

    [Fact]
    public void LastIndexOfF带起始位置()
        => Assert.Equal(1, "abcbc".LastIndexOfF("b", 2));

    // ================= Val =================

    [Fact]
    public void Val解析数字()
        => Assert.Equal(12.5, "12.5".Val());

    [Fact]
    public void Val解析失败返回零()
        => Assert.Equal(0d, "abc".Val());

    [Fact]
    public void TryVal区分零与非法()
    {
        Assert.True("0".TryVal(out var zero));
        Assert.Equal(0d, zero);
        Assert.False("abc".TryVal(out _));
    }

    // ================= 转义 =================

    [Fact]
    public void EscapeXml转义尖括号()
        => Assert.Equal("&lt;a&gt;", "<a>".EscapeXml());

    [Fact]
    public void EscapeXml转义引号与换行()
    {
        Assert.Contains("&quot;", "\"".EscapeXml());
        Assert.Contains("&#xa;", "a\r\nb".EscapeXml());
    }

    [Fact]
    public void EscapeXml以左花括号开头则前置花括号()
        => Assert.StartsWith("{}", "{x}".EscapeXml());

    [Fact]
    public void EscapeLikePattern转义通配符()
        => Assert.Equal("a[*]b[?]c", "a*b?c".EscapeLikePattern());

    [Fact]
    public void EscapeLikePattern保留普通字符()
        => Assert.Equal("abc", "abc".EscapeLikePattern());

    // ================= 正则 =================

    [Fact]
    public void RegexSearch返回全部匹配()
        => Assert.Equal(new[] { "a", "a" }, "a1a".RegexSearch("a"));

    [Fact]
    public void RegexSearch非法正则返回空表()
        => Assert.Empty("abc".RegexSearch("[unclosed"));

    [Fact]
    public void RegexSearch用Regex对象()
        => Assert.Equal(new[] { "12" }, "a12".RegexSearch(new Regex(@"\d+")));

    [Fact]
    public void RegexSeek返回首个匹配()
        => Assert.Equal("12", "a12b34".RegexSeek(@"\d+"));

    [Fact]
    public void RegexSeek无匹配返回null()
        => Assert.Null("abc".RegexSeek(@"\d+"));

    [Fact]
    public void RegexSeek非法正则返回null()
        => Assert.Null("abc".RegexSeek("[unclosed"));

    [Fact]
    public void RegexSeek用Regex对象时从索引零开始匹配()
    {
        // 原版 PCL 把 RegexOptions 当成 startAt 传入 regex.Match(str, (int)options)，
        // 传 IgnoreCase(1) 会从索引 1 开始，丢掉首字符。
        Assert.Equal("12", "12ab".RegexSeek(new Regex(@"\d+")));
    }

    [Fact]
    public void RegexCheck判定匹配()
    {
        Assert.True("abc".RegexCheck("^a"));
        Assert.False("abc".RegexCheck("^b"));
    }

    [Fact]
    public void RegexCheck非法正则返回false()
        => Assert.False("abc".RegexCheck("[unclosed"));

    [Fact]
    public void RegexReplace替换全部()
        => Assert.Equal("x1x2", "a1a2".RegexReplace("a", "x"));

    [Fact]
    public void RegexReplaceEach按匹配逐个替换()
    {
        var seen = 0;
        var r = "a1a2".RegexReplaceEach("a", _ =>
        {
            seen++;
            return "#";
        });
        Assert.Equal("#1#2", r);
        Assert.Equal(2, seen);
    }
}
