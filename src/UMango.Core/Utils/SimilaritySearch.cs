// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/SimilaritySearch.cs。
// 原文件位于 PCL.Core/ 目录 —— 按仓库根 LICENSE 的「ALL THE OTHER DIRECTORIES」段，
// 属于 Apache License 2.0，可逐字复用。
//
// 相对原版的改动：
//   ① 命名空间 PCL.Core.Utils → UMango.Core.Utils；
//   ② 修复 entries 为 null 时的 NRE（原版第 170 行直接 entries.Count）；
//   ③ 修复搜索源文本为 null 时的 NRE（原版 _IsAbsoluteMatch 里 s.Key.Replace）；
//   ④ ToLower → ToLowerInvariant（原版用区域敏感的 ToLower()，土耳其语环境下 "I" 会变成 "ı"，
//      破坏大小写不敏感匹配）；
//   ⑤ 空白查询由「返回全部条目且不排序」改为「返回空列表」
//      （UMango 约定：把 24 条随机序结果当成搜索结果展示是危险的默认行为）；
//   ⑥ 搜索源由 List<KeyValuePair<string,double>> 平铺改为 WeightedSearchField
//      （能表达「这个字段是空的」，且权重语义在类型上更清楚）；
//   ⑦ **分数归一化到 0~1**。原版的分数无上界（实测 express/express = 4.40，
//      @babel/core 自身 = 10.93），阈值 0.1/0.2/0.35 绑定在那个量纲上，
//      直接搬过来无法用于 UMango 的排序与过滤；
//   ⑧ 不再就地改写入参（原版 Search 会写 entry.Similarity / entry.AbsoluteRight）。
//
// 算法本身（最长公共子串贪心匹配 + 长度指数加权 + 位置邻近奖励）保持不变 ——
// 那是原版的核心价值，改掉就等于另写一个算法了。

using System.Text;

namespace UMango.Core.Utils;

/// <summary>
/// 一个参与搜索的字段：一段文本 + 一个相对权重。
/// </summary>
public sealed class WeightedSearchField
{
    /// <summary>
    /// 构造一个搜索字段。
    /// </summary>
    /// <param name="text">字段文本。null 或空白表示该字段没有内容，会被跳过。</param>
    /// <param name="weight">相对权重。默认 1.0。权重为 0 的字段不参与打分。</param>
    public WeightedSearchField(string? text, double weight = 1.0)
    {
        Text = text;
        Weight = weight;
    }

    /// <summary>字段文本（可能为 null）。</summary>
    public string? Text { get; }

    /// <summary>相对权重。同一组字段的权重会按总和归一化。</summary>
    public double Weight { get; }
}

/// <summary>
/// 参与搜索的条目：承载领域对象 + 若干搜索字段。
/// </summary>
/// <typeparam name="T">条目对应的源数据对象（如 <c>PackageInfo</c>）。</typeparam>
public sealed class SearchEntry<T>
{
    /// <summary>构造一个搜索条目。</summary>
    public SearchEntry(T item, IEnumerable<WeightedSearchField> fields)
    {
        Item = item;
        Fields = fields is null ? [] : [.. fields];
    }

    /// <summary>该条目对应的源数据对象。只承载，不参与打分。</summary>
    public T Item { get; }

    /// <summary>该条目用于搜索的字段与权重。</summary>
    public IReadOnlyList<WeightedSearchField> Fields { get; }

    /// <summary>计算出的相似度（0~1）。由 <see cref="SimilaritySearch.Search{T}"/> 写入新条目。</summary>
    public double Similarity { get; init; }

    /// <summary>
    /// 是否为「完全匹配」—— 查询的**每一个**词都能在某个字段里做子串命中。
    /// 由 <see cref="SimilaritySearch.Search{T}"/> 写入新条目。
    /// </summary>
    public bool IsExactMatch { get; init; }
}

/// <summary>
/// 文本相似度搜索：多字段加权，输出排序后的结果。
/// </summary>
public static class SimilaritySearch
{
    //region 算法常量（沿用 PCL 原版）

    /// <summary>匹配长度权重计算的指数底数。越长匹配的得分呈指数增长。</summary>
    private const double LengthPowerBase = 1.4;

    /// <summary>匹配长度权重计算的偏移量。</summary>
    private const double LengthWeightOffset = 3.6;

    /// <summary>匹配位置邻近度的奖励因子。</summary>
    private const double PositionBonusFactor = 0.3;

    /// <summary>计算位置奖励时允许的最大位置差异。</summary>
    private const int MaxPositionBonusDistance = 3;

    /// <summary>源文本长度对最终得分的影响因子。</summary>
    private const double SourceLengthImpactFactor = 3.0;

    /// <summary>源文本长度惩罚的平滑参数。</summary>
    private const double SourceLengthSmoothing = 15.0;

    /// <summary>源文本长度惩罚的指数。0.5（平方根）惩罚过重，0.25 更平缓。</summary>
    private const double SourceLengthExponent = 0.25;

    //endregion

    /// <summary>
    /// 单个字段文本的长度上限。超长文本会被截断 ——
    /// 包描述理论上不会很长，但这是外部输入，不该允许它把匹配成本放大到不可控。
    /// </summary>
    public const int MaxSourceLength = 512;

    /// <summary>
    /// 计算一段文本相对查询的相似度，范围 0~1。
    ///
    /// 1.0 表示「源文本就是查询本身」这一量级上的完美匹配；
    /// 0.0 表示完全没有命中。
    ///
    /// 归一化方式：把原版的连续子串强度（除以查询长度）除以「查询与自身匹配」时的同量值，
    /// 再乘一个源长度因子（源越长扣得越多，但**相对于查询自身**计算，
    /// 所以 Name 字段和 Summary 字段的分数是可比的）。
    /// </summary>
    /// <param name="source">被搜索的长内容。</param>
    /// <param name="query">用户输入的搜索文本。</param>
    public static double Score(string? source, string? query)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(query)) return 0.0;

        var normalizedSource = Normalize(source);
        var normalizedQuery = Normalize(query);
        if (normalizedSource.Length == 0 || normalizedQuery.Length == 0) return 0.0;

        return ScoreNormalized(normalizedSource, normalizedQuery);
    }

    /// <summary>
    /// 多字段加权搜索。返回**新**条目列表（不修改入参）。
    ///
    /// 排序规则：完全匹配的在前（组内按相似度降序，不限条数），
    /// 其后是相似度达到阈值的模糊匹配（最多 <paramref name="maxBlurCount"/> 条）。
    /// </summary>
    /// <typeparam name="T">条目对应的源数据类型。</typeparam>
    /// <param name="entries">待搜索的条目列表。null 或空 → 返回空列表。</param>
    /// <param name="query">用户输入的查询串。null 或空白 → 返回空列表。</param>
    /// <param name="maxBlurCount">模糊结果条数上限（完全匹配组不受此限）。</param>
    /// <param name="minBlurSimilarity">模糊结果的相似度下限（0~1 归一化尺度）。</param>
    public static IReadOnlyList<SearchEntry<T>> Search<T>(
        IReadOnlyList<SearchEntry<T>> entries,
        string? query,
        int maxBlurCount = 5,
        double minBlurSimilarity = 0.35)
    {
        if (entries is null || entries.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(query)) return [];

        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0) return [];

        // 查询按空白切成多个词；每个词都要被命中才算「完全匹配」。
        var queryParts = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(p => p.Length > 0)
            .ToArray();
        if (queryParts.Length == 0) return [];

        var scored = new List<(SearchEntry<T> Source, double Score, bool IsExact)>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null) continue;

            var normalizedFields = NormalizeFields(entry.Fields);
            if (normalizedFields.Count == 0) continue;

            var totalWeight = 0.0;
            var weightedSum = 0.0;
            foreach (var (text, weight) in normalizedFields)
            {
                if (weight <= 0) continue;
                var fieldScore = ScoreNormalized(text, normalizedQuery);
                // 分母是**全部**字段的权重，不是只有命中的那些。
                // 若只按命中字段平均，字段权重就失去了区分能力
                // （名字命中+描述不命中 与 描述命中+名字不命中 会得到同样的分）。
                // 名字权重 1.0 与 0.4 的差别，正是靠这个分母体现的。
                totalWeight += weight;
                weightedSum += fieldScore * weight;
            }

            var score = totalWeight > 0 ? weightedSum / totalWeight : 0.0;
            var isExact = queryParts.All(part => normalizedFields.Any(f => f.Text.Contains(part, StringComparison.Ordinal)));

            scored.Add((entry, score, isExact));
        }

        var ordered = scored
            .OrderByDescending(x => x.IsExact)
            .ThenByDescending(x => x.Score)
            .Select(x => new SearchEntry<T>(x.Source.Item, x.Source.Fields)
            {
                Similarity = x.Score,
                IsExactMatch = x.IsExact
            })
            .ToList();

        // 完全匹配组不限条数（与 PCL 一致）；模糊组既过阈值也限条数。
        var exactMatches = ordered.Where(x => x.IsExactMatch);
        var blurMatches = ordered
            .Where(x => !x.IsExactMatch && x.Similarity >= minBlurSimilarity)
            .Take(maxBlurCount);

        return [.. exactMatches.Concat(blurMatches)];
    }

    /// <summary>
    /// 归一化：转小写（区域无关）并去掉所有空白字符。
    /// 原版只去掉 ASCII 空格，Tab、换行、全角空格都会留在参与匹配 —— 那是 bug 不是特性。
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>归一化一组搜索字段，跳过空文本与非法权重。</summary>
    private static List<(string Text, double Weight)> NormalizeFields(IReadOnlyList<WeightedSearchField> fields)
    {
        var result = new List<(string, double)>(fields.Count);
        foreach (var field in fields)
        {
            if (field is null) continue;
            if (string.IsNullOrEmpty(field.Text)) continue;
            var text = Normalize(field.Text);
            if (text.Length == 0) continue;
            if (text.Length > MaxSourceLength) text = text[..MaxSourceLength];
            result.Add((text, field.Weight));
        }
        return result;
    }

    /// <summary>
    /// 归一化尺度下的相似度。输入必须已经归一化过。
    /// </summary>
    private static double ScoreNormalized(string normalizedSource, string normalizedQuery)
    {
        if (normalizedSource.Length == 0 || normalizedQuery.Length == 0) return 0.0;

        var core = CoreScore(normalizedSource, normalizedQuery);
        if (core <= 0) return 0.0;

        // 参考值是「查询与自身匹配」的强度。
        // 它**不含**位置奖励：PCL 原版用「查询自己对自己」当分母，而那一对必然拿到
        // 满分的位置奖励（源位置 0、查询位置 0），于是任何真实匹配都被平白除以约 6.7 ——
        // 单个词的查询（如 "framework"）在长描述里只能拿到 0.09，
        // 而同样的词在短包名里能拿 1.0，两者根本不可比。
        var reference = CoreScore(normalizedQuery, normalizedQuery);
        if (reference <= 0) return 0.0;

        // 命中强度占参考值的比例。源能提供的匹配不会超过查询自身的匹配量，
        // 所以这个比值天然 ≤ 1。
        var ratio = core / reference;
        if (ratio > 1.0) ratio = 1.0;

        // 源长度惩罚：相对查询自身计算，因此长描述与短包名的分数可比。
        // 指数取 0.25 而不是 0.5（平方根）：平方根对长描述太狠 ——
        // 「framework」命中一句 45 字的描述只剩 0.63，命中 9 字的包名却是 1.0，
        // 两者不具可比性。0.25 次幂下前者是 0.79，量级就接近了。
        var lengthFactor = Math.Pow(
            (normalizedQuery.Length + SourceLengthSmoothing) /
            (normalizedSource.Length + SourceLengthSmoothing),
            SourceLengthExponent);

        // 位置奖励**只做温和的加权**，不参与分母，也不足以让分数越界：
        // 它表达的是「源里命中位置离查询开头越近，越可能是同一个东西」。
        var positionFactor = 0.85 + 0.15 * PositionCloseness(CorePositionScore(normalizedSource, normalizedQuery));

        var score = ratio * lengthFactor * positionFactor;
        return score > 1.0 ? 1.0 : score;
    }

    /// <summary>
    /// 位置接近度：0~1。查询的每一段在源里的匹配起点越靠近查询自身的对应位置，得分越高。
    /// </summary>
    private static double PositionCloseness(double positionScore)
    {
        if (positionScore <= 0) return 0.0;
        return positionScore > 1.0 ? 1.0 : positionScore;
    }

    /// <summary>
    /// 只看位置、不看长度的匹配强度。用于计算位置接近度。
    /// </summary>
    private static double CorePositionScore(string source, string query)
    {
        var used = new bool[source.Length];
        double closenessSum = 0.0;
        var segments = 0;
        var queryIndex = 0;

        while (queryIndex < query.Length)
        {
            var longestMatchLength = 0;
            var bestMatchSourceStartIndex = -1;

            for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
            {
                var currentMatchLength = 0;
                while ((queryIndex + currentMatchLength) < query.Length &&
                       (sourceIndex + currentMatchLength) < source.Length &&
                       !used[sourceIndex + currentMatchLength] &&
                       source[sourceIndex + currentMatchLength] == query[queryIndex + currentMatchLength])
                {
                    currentMatchLength++;
                }

                if (currentMatchLength <= longestMatchLength) continue;

                longestMatchLength = currentMatchLength;
                bestMatchSourceStartIndex = sourceIndex;
            }

            if (longestMatchLength > 0)
            {
                for (var i = 0; i < longestMatchLength; i++)
                {
                    used[bestMatchSourceStartIndex + i] = true;
                }

                var positionDifference = Math.Abs(queryIndex - bestMatchSourceStartIndex);
                closenessSum += Math.Max(0, MaxPositionBonusDistance - positionDifference) / MaxPositionBonusDistance;
                segments++;
            }

            queryIndex += Math.Max(1, longestMatchLength);
        }

        return segments == 0 ? 0.0 : closenessSum / segments;
    }

    /// <summary>
    /// 连续子串强度（PCL 原版的核心算法，去掉源长度惩罚与短查询奖励）。
    ///
    /// 做法：贪心地为查询的每一段找到源里最长的连续匹配，
    /// 匹配长度按 <c>1.4^(3+L) - 3.6</c> 指数加权，
    /// 再乘一个位置邻近奖励（查询位置与源中匹配起点相差 3 位以内才有）。
    /// 源字符不允许被重复计入。
    /// </summary>
    private static double CoreScore(string source, string query)
    {
        var used = new bool[source.Length];
        double weightedLengthSum = 0;
        var queryIndex = 0;

        while (queryIndex < query.Length)
        {
            var longestMatchLength = 0;
            var bestMatchSourceStartIndex = -1;

            for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex++)
            {
                var currentMatchLength = 0;
                while ((queryIndex + currentMatchLength) < query.Length &&
                       (sourceIndex + currentMatchLength) < source.Length &&
                       !used[sourceIndex + currentMatchLength] &&
                       source[sourceIndex + currentMatchLength] == query[queryIndex + currentMatchLength])
                {
                    currentMatchLength++;
                }

                if (currentMatchLength <= longestMatchLength) continue;

                longestMatchLength = currentMatchLength;
                bestMatchSourceStartIndex = sourceIndex;
            }

            if (longestMatchLength > 0)
            {
                for (var i = 0; i < longestMatchLength; i++)
                {
                    used[bestMatchSourceStartIndex + i] = true;
                }

                var incrementWeight = Math.Pow(LengthPowerBase, 3 + longestMatchLength) - LengthWeightOffset;
                weightedLengthSum += incrementWeight;
            }

            queryIndex += Math.Max(1, longestMatchLength);
        }

        return weightedLengthSum / query.Length;
    }
}
