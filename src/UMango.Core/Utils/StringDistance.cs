namespace UMango.Core.Utils;

/// <summary>
/// 字符串相似度计算。
///
/// 对应 PCL-CE 的 Utils/SimilaritySearch.cs（Apache-2.0）。
/// 用于模糊匹配包名（用户输入的查询与实际包名可能有拼写错误）。
///
/// 提供两种算法：
///   · Levenshtein 距离：编辑距离，计算把 A 变成 B 需要的最少操作数
///   · 包含匹配：B 是否是 A 的子串（快速路径）
/// </summary>
public static class StringDistance
{
    /// <summary>
    /// 计算 Levenshtein 编辑距离。
    /// 对称性：Levenshtein(A, B) == Levenshtein(B, A)。
    /// </summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var d = new int[a.Length + 1, b.Length + 1];

        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// 相似度分数（0.0 ~ 1.0）。1.0 表示完全匹配，0.0 表示完全不同。
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / maxLen;
    }

    /// <summary>
    /// 快速包含匹配（子串检查）。
    /// 返回 true 表示 query 是 candidate 的子串（大小写不敏感）。
    /// </summary>
    public static bool ContainsIgnoreCase(string candidate, string query)
    {
        return candidate.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
