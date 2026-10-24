using System.Globalization;
using UMango.Core.Utils;

namespace UMango.Core.Tests.Utils;

public class SimilaritySearchTests
{
    private static SearchEntry<string> Entry(string item, params (string? Text, double Weight)[] fields)
        => new(item, fields.Select(f => new WeightedSearchField(f.Text, f.Weight)));

    #region 边界

    [Fact]
    public void 条目为null时返回空列表而不抛异常()
    {
        var result = SimilaritySearch.Search<string>(null!, "express");
        Assert.Empty(result);
    }

    [Fact]
    public void 条目为空时返回空列表()
    {
        var result = SimilaritySearch.Search(Array.Empty<SearchEntry<string>>(), "express");
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 查询为空白时返回空列表(string? query)
    {
        var entries = new[] { Entry("a", ("express", 1.0)) };
        var result = SimilaritySearch.Search(entries, query);
        Assert.Empty(result);
    }

    [Fact]
    public void 空白查询既不返回结果也不抛异常()
    {
        // 关键：不能因为「查询是空白」就把**全部**条目返回 ——
        // 那会让搜索框一清空，结果列表就突然变成全部包。
        var entries = new[]
        {
            Entry("a", ("express", 1.0)),
            Entry("b", ("koa", 1.0))
        };

        foreach (var q in new[] { "", "   ", "\t", "\n" })
        {
            var result = SimilaritySearch.Search(entries, q);
            Assert.Empty(result);
        }
    }

    [Fact]
    public void 只有空白的查询不返回结果()
    {
        var entries = new[] { Entry("a", ("express", 1.0)) };
        var result = SimilaritySearch.Search(entries, " \t\n ");
        Assert.Empty(result);
    }

    [Fact]
    public void 字段文本为null时该字段被跳过而不抛异常()
    {
        var entries = new[] { Entry("a", ((string?)null, 1.0), ("express", 1.0)) };
        var result = SimilaritySearch.Search(entries, "express");
        Assert.Single(result);
        Assert.Equal("a", result[0].Item);
    }

    [Fact]
    public void 权重全为零时相似度为零且没有模糊结果()
    {
        var entries = new[] { Entry("a", ("express", 0.0)) };
        var result = SimilaritySearch.Search(entries, "express");
        // 完全匹配仍然算命中（子串判定不看权重），但相似度必须是 0
        Assert.Single(result);
        Assert.Equal(0.0, result[0].Similarity, 6);
    }

    [Fact]
    public void 不修改传入的条目()
    {
        var entry = Entry("a", ("express", 1.0));
        SimilaritySearch.Search(new[] { entry }, "express");
        Assert.Equal(0.0, entry.Similarity);
        Assert.False(entry.IsExactMatch);
    }

    #endregion

    #region 归一化尺度

    [Fact]
    public void 位置奖励不参与分母()
    {
        // 位置奖励是「源里命中位置离查询开头越近越可信」的加权，
        // 它一旦进了分母，真实匹配的分数就会被平白除掉一大截。
        // 这里锁定：查询词出现在源的开头时，分数必须明显高于
        // 同一个词出现在源的末尾时。
        var atStart = SimilaritySearch.Score("framework and more text", "framework");
        var atEnd = SimilaritySearch.Score("some text and more words before framework", "framework");

        Assert.True(
            atStart > atEnd,
            $"命中在开头应优于命中在末尾：{atStart:F4} vs {atEnd:F4}");
    }

    [Fact]
    public void 位置接近度确实影响分数()
    {
        // 位置因子必须真的起作用，而不是恒等于 1。
        // 命中在源的开头 vs 结尾，两者的分数差必须大于零。
        var atStart = SimilaritySearch.Score("framework here", "framework");
        var atEnd = SimilaritySearch.Score("here framework", "framework");

        Assert.True(
            Math.Abs(atStart - atEnd) > 0.001,
            $"位置因子应造成可见分差，实际 {atStart:F6} vs {atEnd:F6}");
    }

    [Fact]
    public void 分数落在0到1之间()
    {
        var samples = new[]
        {
            ("express", "express"),
            ("express-cookie-parser", "express"),
            ("A fast web framework for Node.js", "framework"),
            ("@babel/core", "@babel/core"),
            ("完全不相干的东西", "express")
        };

        foreach (var (source, query) in samples)
        {
            var score = SimilaritySearch.Score(source, query);
            Assert.InRange(score, 0.0, 1.0);
        }
    }

    [Fact]
    public void 完全相同的文本得到满分()
    {
        Assert.Equal(1.0, SimilaritySearch.Score("express", "express"), 6);
    }

    [Fact]
    public void 长描述与短包名的分数可比()
    {
        // 同一段文字放在长描述里，不应因为源更长就被压到远低于短包名的分数。
        var shortName = SimilaritySearch.Score("framework", "framework");
        var longSummary = SimilaritySearch.Score("A fast, unopinionated web framework for Node.js", "framework");

        Assert.InRange(longSummary, shortName * 0.5, shortName * 1.5);
    }

    [Fact]
    public void 长描述的分数不应被顶到满分()
    {
        // 命中一个词却拿满分，等于说「这段 45 字的描述和 "framework" 完全一样」，
        // 那会让真正的包名匹配无法排在它前面。
        var longSummary = SimilaritySearch.Score("A fast, unopinionated web framework for Node.js", "framework");
        Assert.True(longSummary < 1.0, $"长描述不该拿满分，实际 {longSummary}");
    }

    [Fact]
    public void 长描述的分数至少是短包名分数的六成半()
    {
        // 源长度惩罚若用平方根（0.5 次幂），长描述只剩 0.56 —— 明显偏低。
        // 这里锁定 0.25 次幂的量级。
        var shortName = SimilaritySearch.Score("framework", "framework");
        var longSummary = SimilaritySearch.Score("A fast, unopinionated web framework for Node.js", "framework");

        Assert.True(
            longSummary >= shortName * 0.65,
            $"长描述分数 {longSummary:F4} 相对短包名 {shortName:F4} 太低了");
    }

    [Fact]
    public void 土耳其语区域下大小写仍然不影响分数()
    {
        // 土耳其语把 "I" 的小写作成 "ı"（带点的小写 i），
        // 区域敏感的 ToLower 会把 "I" 变成 "ı"，于是 "I" 再也匹配不上 "i"。
        // 所以归一化必须用 ToLowerInvariant。
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            // 必须用带 "I" 的文本：土耳其语把 'I' 小写成 'ı'（带点的小写 i），
            // 区域敏感的 ToLower 会让 "IExpress" 变成 "ıexpress"，
            // 于是再也匹配不上 "iexpress"。只含 "E" 的 "Express" 是测不出来的。
            Assert.Equal(
                SimilaritySearch.Score("IExpress", "iexpress"),
                SimilaritySearch.Score("iexpress", "iexpress"),
                6);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void 大小写不影响分数()
    {
        Assert.Equal(
            SimilaritySearch.Score("Express", "express"),
            SimilaritySearch.Score("express", "express"),
            6);
    }

    [Fact]
    public void 空白字符被忽略()
    {
        // 原版只去掉 ASCII 空格，Tab/换行/全角空格都会留在参与匹配。
        Assert.Equal(
            SimilaritySearch.Score("express", "express"),
            SimilaritySearch.Score("ex\tpress", "express"),
            6);
        Assert.Equal(
            SimilaritySearch.Score("express", "express"),
            SimilaritySearch.Score("ex　press", "express"),
            6);
    }

    #endregion

    #region 排序

    [Fact]
    public void 精确匹配排在最前()
    {
        var entries = new[]
        {
            Entry("express-cookie-parser", ("express-cookie-parser", 1.0)),
            Entry("express", ("express", 1.0)),
            Entry("fastify", ("fastify", 1.0))
        };

        var result = SimilaritySearch.Search(entries, "express");
        Assert.Equal("express", result[0].Item);
    }

    [Fact]
    public void 精确匹配组内按相似度降序()
    {
        var entries = new[]
        {
            Entry("express-session", ("express-session", 1.0)),
            Entry("express", ("express", 1.0))
        };

        var result = SimilaritySearch.Search(entries, "express");
        Assert.Equal("express", result[0].Item);
        Assert.Equal("express-session", result[1].Item);
    }

    [Fact]
    public void 少了一个字母不算精确匹配()
    {
        // 注意：查询是源文本的**子串**时（"expres" ⊂ "express"）算精确匹配，
        // 因为用户就是在找一个更短的包名。真正该排除的是「字符对不上」的情况。
        var entries = new[] { Entry("express", ("express", 1.0)) };
        var result = SimilaritySearch.Search(entries, "exprss");
        Assert.DoesNotContain(result, r => r.IsExactMatch);
    }

    [Fact]
    public void 多词查询可以在不同字段分别命中()
    {
        var entries = new[]
        {
            Entry("pkg", ("web", 1.0), ("framework", 1.0))
        };

        var result = SimilaritySearch.Search(entries, "web framework");
        Assert.Single(result);
        Assert.True(result[0].IsExactMatch);
    }

    [Fact]
    public void 模糊结果有数量上限而精确匹配不受限()
    {
        var entries = new List<SearchEntry<string>>();
        for (var i = 0; i < 20; i++)
        {
            entries.Add(Entry($"fuzzy{i}", ($"fuzzy{i}-something", 1.0)));
        }
        entries.Add(Entry("exact", ("express", 1.0)));

        var result = SimilaritySearch.Search(entries, "express", maxBlurCount: 3);
        Assert.Single(result);
        Assert.Equal("exact", result[0].Item);
    }

    [Fact]
    public void 相同分数保持原有顺序()
    {
        var entries = new[]
        {
            Entry("first", ("same", 1.0)),
            Entry("second", ("same", 1.0)),
            Entry("third", ("same", 1.0))
        };

        var result = SimilaritySearch.Search(entries, "same");
        Assert.Equal(["first", "second", "third"], result.Select(r => r.Item));
    }

    [Fact]
    public void 阈值边界包含等于阈值的结果()
    {
        var entries = new[] { Entry("a", ("express", 1.0)) };
        var score = SimilaritySearch.Score("express", "express");
        var result = SimilaritySearch.Search(entries, "express", minBlurSimilarity: score);
        Assert.Single(result);
    }

    #endregion

    #region 多字段加权

    [Fact]
    public void 权重决定字段在总分里的占比()
    {
        // 名字命中、描述不命中：名字权重越高，总分越高。
        var nameHeavy = new SearchEntry<string>("x",
        [
            new WeightedSearchField("express", 1.0),
            new WeightedSearchField("完全无关", 0.4)
        ]);
        var descHeavy = new SearchEntry<string>("x",
        [
            new WeightedSearchField("express", 0.4),
            new WeightedSearchField("完全无关", 1.0)
        ]);

        var nameScore = SimilaritySearch.Search(new[] { nameHeavy }, "express")[0].Similarity;
        var descScore = SimilaritySearch.Search(new[] { descHeavy }, "express")[0].Similarity;

        Assert.True(nameScore > descScore, $"名字权重高时分数应更高：{nameScore} vs {descScore}");
    }

    [Fact]
    public void 描述命中能救回名字不中的条目()
    {
        // 查询 "web framework"，包名是 "pkg"，描述里有 "web framework"。
        var entries = new[]
        {
            Entry("pkg", ("pkg", 1.0), ("A fast web framework", 0.4))
        };

        var result = SimilaritySearch.Search(entries, "web framework");
        Assert.Single(result);
        Assert.True(result[0].IsExactMatch);
    }

    [Fact]
    public void 没有字段的条目不会出现在结果里()
    {
        var entries = new[] { Entry("a") };
        var result = SimilaritySearch.Search(entries, "express");
        Assert.Empty(result);
    }

    #endregion
}
