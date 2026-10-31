using System.Text.RegularExpressions;

namespace UMango.Tools.LocalizationAudit;

/// <summary>
/// 语言文件（<c>*.txt</c>，真相源）里某个键的模板信息。
/// </summary>
/// <param name="Key">键名。</param>
/// <param name="Template">模板原文（已解转义）。</param>
/// <param name="Language">所属语言代码。</param>
/// <param name="PlaceholderIndices">
/// 模板里用到的占位符索引集合（<c>{0} {1}</c> → <c>{0,1}</c>）。
/// </param>
public sealed record LanguageEntry(string Key, string Template, string Language, IReadOnlyList<int> PlaceholderIndices)
{
    /// <summary>
    /// <c>string.Format</c> 至少要几个实参才能不抛 <see cref="FormatException"/>。
    /// 取最大索引 + 1（<c>{0} {2}</c> 需要 3 个 —— 索引 2 越界同样会抛）。
    /// </summary>
    public int RequiredArgCount => PlaceholderIndices.Count == 0 ? 0 : PlaceholderIndices.Max() + 1;
}

/// <summary>
/// 语言文件的读取与占位符分析。
/// </summary>
public static class LanguageFiles
{
    /// <summary>匹配 <c>{数字}</c> 形式的占位符；负向回顾跳过 <c>{{</c> 转义。</summary>
    private static readonly Regex PlaceholderPattern = new(@"(?<!\{)\{(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// 解析纯文本语言文件 → 键 → 模板。
    ///
    /// <para>
    /// 格式与 <c>UMango.Shell.Localization.LanguageCatalog.Parse</c> 一致：
    /// <c>键=值</c>，<c>#</c> 开头是注释，值里 <c>\n</c> / <c>\t</c> / <c>\\</c> 是转义。
    /// 两边各读一遍容易漂，所以这里刻意只做本工具需要的最小解析。
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.TrimStart().StartsWith('#')) continue;
            if (raw.Trim().Length == 0) continue;

            var separator = raw.IndexOf('=');
            if (separator < 0) continue;

            var key = raw[..separator].Trim();
            if (key.Length == 0) continue;
            entries[key] = Unescape(raw[(separator + 1)..]);
        }
        return entries;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
            var next = value[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(next); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>取出模板里用到的占位符索引（去重、升序）。</summary>
    public static IReadOnlyList<int> PlaceholderIndices(string template)
    {
        var set = new SortedSet<int>();
        foreach (Match m in PlaceholderPattern.Matches(template))
        {
            if (int.TryParse(m.Groups[1].Value, out var index)) set.Add(index);
        }
        return set.ToList();
    }
}
