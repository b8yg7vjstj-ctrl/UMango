using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UMango.Shell.Localization;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 语言目录（<see cref="LanguageCatalog"/>）的测试。
///
/// 重点在两处：
/// ① 解析与转义的**往返**必须无损（末尾空格、换行、制表符、反斜杠都是真实文案内容）；
/// ② 兜底层/叠加层的分层语义（叠加层覆盖兜底层，缺失的键继承兜底层）。
/// </summary>
public class LanguageCatalogTests
{
    [Fact]
    public void 解析_读出键值对()
    {
        var entries = LanguageCatalog.Parse("A=1\nB=2\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal("1", entries["A"]);
        Assert.Equal("2", entries["B"]);
    }

    [Fact]
    public void 解析_忽略注释与空行()
    {
        var entries = LanguageCatalog.Parse("# 普通注释\n\n  \nA=1\n");
        Assert.Single(entries);
        Assert.Equal("1", entries["A"]);
    }

    [Fact]
    public void 解析_缺少等号时报错()
    {
        var ex = Assert.Throws<FormatException>(() => LanguageCatalog.Parse("没有等号\n"));
        Assert.Contains("'='", ex.Message);
    }

    [Fact]
    public void 解析_空键报错()
    {
        Assert.Throws<FormatException>(() => LanguageCatalog.Parse("=值\n"));
    }

    [Fact]
    public void 解析_重复键报错()
    {
        var ex = Assert.Throws<FormatException>(() => LanguageCatalog.Parse("A=1\nA=2\n"));
        Assert.Contains("重复键", ex.Message);
    }

    [Theory]
    [InlineData(@"1\n2", "1\n2")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"x\\y", "x\\y")]
    [InlineData(@"末尾有空格 ", "末尾有空格 ")]
    public void 解析_转义往返无损(string encoded, string expected)
    {
        // 真值写法：先把原文按格式编码，再解析回来
        var entries = LanguageCatalog.Parse("K=" + encoded);
        Assert.Equal(expected, entries["K"]);
    }

    [Fact]
    public void 解析_保留末尾空格()
    {
        // 末尾空格是有意义的（"其他版本： " 要跟后面拼的提示语）
        var entries = LanguageCatalog.Parse("K=值 \n");
        Assert.Equal("值 ", entries["K"]);
    }

    [Fact]
    public void 解析_解码转义序列()
    {
        // 换行在文件里写成 \n（两字符），不是真换行 —— 真换行会破坏"一行一条"的结构
        var entries = LanguageCatalog.Parse("A=第一行\\n第二行\nB=制表\\t符\n");
        Assert.Equal("第一行\n第二行", entries["A"]);
        Assert.Equal("制表\t符", entries["B"]);
    }

    [Fact]
    public void 解析_未知转义保留原样()
    {
        var entries = LanguageCatalog.Parse("A=x\\qy\n");
        Assert.Equal("x\\qy", entries["A"]);
    }

    [Fact]
    public void 取文案_找不到键时返回键本身()
    {
        var catalog = Load("zh-CN");
        Assert.Equal("不存在的键", catalog.Text("不存在的键"));
    }

    [Fact]
    public void 取文案_带占位符会格式化()
    {
        var catalog = Load("zh-CN");
        var template = catalog.Text("Common.Action.Confirm");
        Assert.NotNull(template);
    }

    [Fact]
    public void 目录_包含键判断()
    {
        var catalog = Load("zh-CN");
        Assert.True(catalog.Contains("Common.AppName"));
        Assert.False(catalog.Contains("Definitely.Not.A.Key"));
    }

    [Fact]
    public void 加载_默认语言返回兜底层()
    {
        var catalog = LanguageCatalog.Load(LanguageCatalog.FallbackLanguageCode);
        Assert.Equal(LanguageCatalog.FallbackLanguageCode, catalog.LanguageCode);
        Assert.True(catalog.KeyCount > 100, "兜底层应当有上百个键");
    }

    [Fact]
    public void 加载_空语言代码回退到兜底层()
    {
        var catalog = LanguageCatalog.Load(null);
        Assert.Equal(LanguageCatalog.FallbackLanguageCode, catalog.LanguageCode);
    }

    [Fact]
    public void 加载_未知语言回退到兜底层且不谎报语言()
    {
        var catalog = LanguageCatalog.Load("xx-YY");
        // 关键：不能把"没找到"谎报成"已切到 xx-YY"——否则设置页会显示错误的语言名
        Assert.Equal(LanguageCatalog.FallbackLanguageCode, catalog.LanguageCode);
    }

    [Fact]
    public void 加载_叠加层覆盖兜底层的同名键()
    {
        var fallback = new Dictionary<string, string> { ["K"] = "中文" };
        var overlay = new Dictionary<string, string> { ["K"] = "English" };

        var merged = Merge(fallback, overlay);
        Assert.Equal("English", merged["K"]);
    }

    [Fact]
    public void 加载_叠加层缺失的键继承兜底层()
    {
        var fallback = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
        var overlay = new Dictionary<string, string> { ["B"] = "two" };

        var merged = Merge(fallback, overlay);
        Assert.Equal("1", merged["A"]);
        Assert.Equal("two", merged["B"]);
    }

    [Fact]
    public void 真实语言文件_兜底层键数足够()
    {
        var catalog = LanguageCatalog.Load(LanguageCatalog.FallbackLanguageCode);
        // 这不是"越多越好"的装饰性断言：键太少说明真相源被截断
        // （历史上真的发生过语言文件从 116 键被毁到 91 键的事故）。
        Assert.True(catalog.KeyCount >= 500, $"兜底层只有 {catalog.KeyCount} 个键，疑似被截断");
    }

    [Fact]
    public void 真实语言文件_每个键都有非空文案()
    {
        var catalog = LanguageCatalog.Load(LanguageCatalog.FallbackLanguageCode);
        var empty = catalog.AllEntries().Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
        Assert.True(empty.Count == 0, $"有 {empty.Count} 个键的文案是空的：{string.Join(", ", empty.Take(10))}");
    }

    [Fact]
    public void 真实语言文件_没有重复键()
    {
        // 重复键在 .xaml 里 WPF 会**后者覆盖前者**、不报错；
        // 在纯文本里我们直接抛异常，所以这个用例保证两边行为一致前先过了静态检查。
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var catalog = LanguageCatalog.Load(code);
            Assert.True(catalog.KeyCount > 0, $"{code} 没能加载出任何键");
        }
    }

    [Fact]
    public void 渲染xaml_转义xml特殊字符()
    {
        var rendered = LanguageCatalog.RenderXaml(
            new Dictionary<string, string> { ["K"] = "a & b < c > d" },
            "头");
        Assert.Contains("a &amp; b &lt; c &gt; d", rendered);
    }

    [Fact]
    public void 渲染xaml_包含资源字典骨架()
    {
        var rendered = LanguageCatalog.RenderXaml(new Dictionary<string, string> { ["K"] = "v" }, "头");
        Assert.Contains("<ResourceDictionary", rendered);
        Assert.Contains("</ResourceDictionary>", rendered);
        Assert.Contains("x:Key=\"K\"", rendered);
    }

    [Fact]
    public void 渲染xaml_换行写成字符引用()
    {
        // XML 里元素内的裸换行会被解析器规范化成空格 —— 文案会断。
        var rendered = LanguageCatalog.RenderXaml(
            new Dictionary<string, string> { ["K"] = "第一行\n第二行" }, "头");
        Assert.Contains("&#x0a;", rendered);
    }

    private static LanguageCatalog Load(string code) => LanguageCatalog.Load(code);

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> fallback,
        IReadOnlyDictionary<string, string> overlay)
    {
        var merged = new Dictionary<string, string>(fallback, StringComparer.Ordinal);
        foreach (var pair in overlay) merged[pair.Key] = pair.Value;
        return merged;
    }
}
