using UMango.Core.PackageManagers;

namespace UMango.Core.Tests.PackageManagers;

public class PackageSearchRankingTests
{
    private static PackageInfo Pkg(string name, string summary = "", string version = "1.0.0")
        => new()
        {
            Source = PackageSourceKind.Npm,
            Name = name,
            Summary = summary,
            LatestVersion = version
        };

    [Fact]
    public void 包为null时抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PackageSearchRanking.ToEntry(null!));
    }

    [Fact]
    public void 字段权重是名字1_描述0_4_版本0_2()
    {
        Assert.Equal(1.0, PackageSearchRanking.NameWeight);
        Assert.Equal(0.4, PackageSearchRanking.SummaryWeight);
        Assert.Equal(0.2, PackageSearchRanking.VersionWeight);
    }

    [Fact]
    public void 映射出的条目含三个字段()
    {
        var entry = PackageSearchRanking.ToEntry(Pkg("express", "fast framework", "4.18.2"));
        Assert.Equal(3, entry.Fields.Count);
        Assert.Equal("express", entry.Fields[0].Text);
        Assert.Equal("fast framework", entry.Fields[1].Text);
        Assert.Equal("4.18.2", entry.Fields[2].Text);
    }

    [Fact]
    public void 空的包列表返回空()
    {
        Assert.Empty(PackageSearchRanking.Rank([], "express"));
    }

    [Fact]
    public void null包列表返回空()
    {
        Assert.Empty(PackageSearchRanking.Rank(null!, "express"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 空白查询返回空(string? query)
    {
        var packages = new[] { Pkg("express") };
        Assert.Empty(PackageSearchRanking.Rank(packages, query));
    }

    [Fact]
    public void 名字命中的包排在描述命中的包之前()
    {
        var packages = new[]
        {
            Pkg("other", "express style helper"),
            Pkg("express", "a web framework")
        };

        var ranked = PackageSearchRanking.Rank(packages, "express");
        Assert.Equal("express", ranked[0].Name);
    }

    [Fact]
    public void 描述命中也能搜出包()
    {
        var packages = new[]
        {
            Pkg("pkg-a", "a fast web framework"),
            Pkg("pkg-b", "unrelated")
        };

        var ranked = PackageSearchRanking.Rank(packages, "framework");
        Assert.Contains("pkg-a", ranked.Select(p => p.Name));
    }

    [Fact]
    public void 结果不含输入的包()
    {
        var packages = new[] { Pkg("express") };
        var ranked = PackageSearchRanking.Rank(packages, "express");
        Assert.Single(ranked);
        Assert.Equal("express", ranked[0].Name);
    }

    [Fact]
    public void 不修改输入列表()
    {
        var packages = new List<PackageInfo> { Pkg("express") };
        PackageSearchRanking.Rank(packages, "express");
        Assert.Single(packages);
    }

    [Fact]
    public void 完全匹配的包不限条数()
    {
        var packages = Enumerable.Range(0, 30).Select(i => Pkg($"express-{i}")).ToList();
        var ranked = PackageSearchRanking.Rank(packages, "express", maxBlurCount: 3);
        Assert.Equal(30, ranked.Count);
    }

    [Fact]
    public void 模糊结果有数量上限()
    {
        // 查询两个词，其中只有 "express" 出现在描述里，"framework" 谁都不沾 ——
        // 所以这些条目**不是**完全匹配，只能走模糊通道，数量因此受限。
        var packages = Enumerable.Range(0, 30)
            .Select(i => Pkg($"pkg-{i}", $"contains express helper number {i}"))
            .ToList();

        var ranked = PackageSearchRanking.Rank(packages, "express framework", maxBlurCount: 5);
        Assert.True(ranked.Count <= 5, $"模糊结果应受限，实际 {ranked.Count}");
    }

    [Fact]
    public void 阈值过滤掉不相关的包()
    {
        var packages = new[]
        {
            Pkg("alpha", "totally different topic"),
            Pkg("express", "exact")
        };

        var ranked = PackageSearchRanking.Rank(packages, "express", minBlurSimilarity: 0.9);
        Assert.Contains("express", ranked.Select(p => p.Name));
    }
}
