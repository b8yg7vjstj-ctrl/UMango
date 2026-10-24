using UMango.Core.Utils;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 包搜索排序：把 <see cref="PackageInfo"/> 映射成多字段加权的搜索条目，并按相关度排序。
///
/// 这一层放包管理域而不是 UI 层，是因为「哪些字段参与搜索、各自权重多少」
/// 是包语义的 knowledge，不该散落在 ViewModel 里。
/// </summary>
public static class PackageSearchRanking
{
    /// <summary>包名字段权重。PCL 的惯例：主标识 = 1.0。</summary>
    public const double NameWeight = 1.0;

    /// <summary>描述字段权重。PCL 的惯例：描述 = 0.4。</summary>
    public const double SummaryWeight = 0.4;

    /// <summary>版本号字段权重。PCL 的惯例：版本 = 0.2。</summary>
    public const double VersionWeight = 0.2;

    /// <summary>
    /// 默认的模糊结果条数上限。
    /// UMango 的搜索结果上限由 <c>DownloadViewModel.SearchLimit</c>（24）控制，
    /// 这里给一个与之相称的默认值。
    /// </summary>
    public const int DefaultMaxBlurCount = 24;

    /// <summary>
    /// 默认的模糊匹配相似度下限（0~1 归一化尺度）。
    /// </summary>
    public const double DefaultMinBlurSimilarity = 0.35;

    /// <summary>
    /// 把一个包映射成搜索条目：包名 1.0、描述 0.4、版本 0.2。
    /// </summary>
    public static SearchEntry<PackageInfo> ToEntry(PackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new SearchEntry<PackageInfo>(package,
        [
            new WeightedSearchField(package.Name, NameWeight),
            new WeightedSearchField(package.Summary, SummaryWeight),
            new WeightedSearchField(package.LatestVersion, VersionWeight)
        ]);
    }

    /// <summary>
    /// 按相关度排序一批搜索结果。
    ///
    /// 完全匹配的（查询的每个词都能在某个字段里子串命中）排在最前，
    /// 组内按相似度降序；其后是相似度达标的模糊匹配。
    /// </summary>
    public static IReadOnlyList<PackageInfo> Rank(
        IReadOnlyList<PackageInfo> packages,
        string? query,
        int maxBlurCount = DefaultMaxBlurCount,
        double minBlurSimilarity = DefaultMinBlurSimilarity)
    {
        if (packages is null || packages.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(query)) return [];

        var entries = packages.Select(ToEntry).ToList();
        return [.. SimilaritySearch.Search(entries, query, maxBlurCount, minBlurSimilarity)
            .Select(e => e.Item)];
    }

    /// <summary>
    /// 只重排、**不丢结果**：把一批包按与查询的相关度排序。
    ///
    /// 与 <see cref="Rank"/> 的区别：<see cref="Rank"/> 是「搜索」（有阈值过滤与条数上限），
    /// 这里是对**已经拿到的结果**重新排序 —— 包管理器 CLI 返回什么，用户就该看到什么，
    /// 只是顺序更合理。因此这里把阈值设成 0、条数上限设成无穷。
    /// </summary>
    public static IReadOnlyList<PackageInfo> Sort(
        IReadOnlyList<PackageInfo> packages,
        string? query)
    {
        if (packages is null || packages.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(query)) return packages;

        var entries = packages.Select(ToEntry).ToList();
        return [.. SimilaritySearch.Search(entries, query, int.MaxValue, 0.0)
            .Select(e => e.Item)];
    }
}
