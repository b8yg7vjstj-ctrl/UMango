using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UMango.Shell.Localization;

/// <summary>
/// 界面文案的**唯一真相源**（平台中立）。
///
/// <para>
/// 文案本身是界面内容，与 UI 框架无关 —— 但 <c>.xaml</c> 资源字典只有 WPF 读得懂
/// （<c>sys:String</c> / <c>mscorlib</c> 命名空间在 Avalonia 里根本不存在）。
/// 所以文案被降级成极简纯文本 <c>键=值</c>，由本类解析：
/// </para>
/// <list type="bullet">
///   <item>WPF 壳把它灌进 <c>Application.Resources</c>（<c>{DynamicResource 键}</c> 照常工作）；</item>
///   <item>Avalonia 壳把它灌进 <c>Application.Resources</c>。</item>
/// </list>
///
/// <para>
/// 两边读**同一份内嵌资源**，不可能再出现"一边改了另一边没改"。
/// WPF 侧的 <c>.xaml</c> 字典是由 <c>scripts/sync-language-xaml.py</c> 从这些
/// <c>.txt</c> 生成的**派生产物**，并有测试守着两边一致。
/// </para>
///
/// <para>
/// <b>分层模型</b>（沿用 PCL-CE <c>LocalizationService</c> 的设计，Apache-2.0）：
/// ① <c>zh-CN</c> = 兜底层，<b>始终</b>加载，键最全；
/// ② 其它语言 = 叠加层，只写"变了的键"，重复键覆盖兜底层。
/// </para>
/// </summary>
public sealed class LanguageCatalog
{
    /// <summary>兜底层语言代码（始终加载）。</summary>
    public const string FallbackLanguageCode = "zh-CN";

    /// <summary>内嵌资源的名字前缀（<c>UMango.Shell.Localization.Languages.{code}.txt</c>）。</summary>
    private const string ResourcePrefix = "UMango.Shell.Localization.Languages.";

    private readonly IReadOnlyDictionary<string, string> _fallback;
    private readonly IReadOnlyDictionary<string, string> _overlay;

    private LanguageCatalog(
        string languageCode,
        IReadOnlyDictionary<string, string> fallback,
        IReadOnlyDictionary<string, string> overlay)
    {
        LanguageCode = languageCode;
        _fallback = fallback;
        _overlay = overlay;
    }

    /// <summary>当前语言代码（已解析为具体语言，不会是"跟随系统"）。</summary>
    public string LanguageCode { get; }

    /// <summary>兜底层的键数。</summary>
    public int FallbackKeyCount => _fallback.Count;

    /// <summary>叠加层的键数。</summary>
    public int OverlayKeyCount => _overlay.Count;

    /// <summary>最终可见的键数（兜底 ∪ 叠加）。</summary>
    public int KeyCount => Merged.Count;

    private IReadOnlyDictionary<string, string> Merged
    {
        get
        {
            var merged = new Dictionary<string, string>(_fallback, StringComparer.Ordinal);
            foreach (var pair in _overlay) merged[pair.Key] = pair.Value;
            return merged;
        }
    }

    /// <summary>取文案；找不到键时返回键本身（与既有 <c>PCL.Lang</c> 行为一致）。</summary>
    public string Text(string key) => Text(key, null);

    /// <summary>取文案并按 <see cref="CultureInfo.CurrentCulture"/> 格式化占位符。</summary>
    public string Text(string key, params object?[]? args)
    {
        if (!Merged.TryGetValue(key, out var template)) return key;
        if (args is null || args.Length == 0) return template;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 占位符与参数对不上（文案改了、调用点没改）不应把界面搞崩：
            // 原样返回模板，让人看得见问题。
            return template;
        }
    }

    /// <summary>该键是否存在于当前语言的可见集合里。</summary>
    public bool Contains(string key) => Merged.ContainsKey(key);

    /// <summary>当前可见的全部键值（供 UI 框架灌资源字典）。</summary>
    public IEnumerable<KeyValuePair<string, string>> AllEntries() => Merged;

    /// <summary>解析指定语言的目录（<paramref name="languageCode"/> 为空/未知时回退到兜底层）。</summary>
    public static LanguageCatalog Load(string? languageCode)
    {
        var fallback = LoadRaw(FallbackLanguageCode) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(languageCode) ||
            string.Equals(languageCode, FallbackLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return new LanguageCatalog(FallbackLanguageCode, fallback, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var overlay = LoadRaw(languageCode);
        if (overlay is null || overlay.Count == 0)
        {
            // 语言文件不存在 → 只用兜底层（并且**不**谎报语言已切换）
            return new LanguageCatalog(FallbackLanguageCode, fallback, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return new LanguageCatalog(languageCode, fallback, overlay);
    }

    /// <summary>读内嵌的纯文本语言文件；找不到返回 null。</summary>
    private static IReadOnlyDictionary<string, string>? LoadRaw(string languageCode)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        var name = ResourcePrefix + languageCode + ".txt";
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// 解析纯文本格式：<c>键=值</c>，空行与 <c>#</c> 开头为注释。
    /// 值里的换行/制表/反斜杠用 <c>\n</c> / <c>\t</c> / <c>\\</c> 转义。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            // 注意：**不能 TrimEnd 整行** —— 有些文案的末尾空格是有意义的
            // （例如 "其他版本： " 这种要跟后面拼的提示语）。
            // 换行符已在上面规范化掉，raw 本身不含行尾 \r。
            var line = raw;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            // 全空白行忽略（但**保留**有内容的行里的末尾空格）
            if (trimmed.Length == 0) continue;

            var separator = line.IndexOf('=');
            if (separator < 0)
                throw new FormatException($"语言文件格式错误（缺少 '='）：{line}");

            var key = line[..separator].Trim();
            if (key.Length == 0)
                throw new FormatException($"语言文件格式错误（空键）：{line}");

            if (entries.ContainsKey(key))
                throw new FormatException($"语言文件里有重复键：{key}");

            entries[key] = Unescape(line[(separator + 1)..]);
        }

        return entries;
    }

    /// <summary>解码 <c>\n</c> / <c>\r</c> / <c>\t</c> / <c>\\</c>。</summary>
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
                default:
                    // 未知转义：原样保留（不吞字符）
                    sb.Append('\\').Append(next);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把纯文本条目渲染回 WPF 的 <c>.xaml</c> 资源字典（派生产物）。
    /// WPF 侧的 <c>Localization/Languages/*.xaml</c> 由脚本从这里生成。
    /// </summary>
    public static string RenderXaml(IReadOnlyDictionary<string, string> entries, string commentHeader)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!--");
        sb.AppendLine(commentHeader);
        sb.AppendLine("-->");
        sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        sb.AppendLine("                    xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">");
        sb.AppendLine();
        foreach (var pair in entries)
        {
            sb.AppendLine($"    <sys:String x:Key=\"{pair.Key}\">{EscapeXml(pair.Value)}</sys:String>");
        }

        sb.AppendLine("</ResourceDictionary>");
        return sb.ToString();
    }

    /// <summary>XML 文本转义（XAML 里 <c>&amp;</c>、<c>&lt;</c> 必须转义）。</summary>
    /// <remarks>
    /// 换行必须写成字符引用 <c>&amp;#x0a;</c>：XML 里元素内的裸换行会被 XAML 解析器
    /// 规范化成空格（<c>xml:space="preserve"</c> 之外的默认行为），文案就断了。
    /// </remarks>
    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\n", "&#x0a;")
        .Replace("\r", "&#x0d;");
}
