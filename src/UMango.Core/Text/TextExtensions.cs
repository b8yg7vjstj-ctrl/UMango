// SPDX-License-Identifier: Apache-2.0
//
// 字符串工具集 —— 独立实现。
//
// ⚠️ 设计来源：PCL-CE `Plain Craft Launcher 2/Modules/Base/ModBase.cs:1624-2020`
//    的文本扩展方法块（**受限许可目录** → 只借鉴公开的行为语义，
//    不复制代码、不逐字迁移）。
//
// 与 PCL 原版的**行为差异**（有意为之，逐条列出）：
//   ① 命名空间与类名不同（`UMango.Core.Text.TextExtensions`），
//      避免与既有 `UMango.Core.Utils.Extensions` 的 `Between`/`AfterFirst`/
//      `BeforeLast` 产生歧义调用。
//   ② `Between(after, before)` 采用 PCL 语义：**after 取最后一次出现**、
//      before 取其后第一次出现（"裁切尽可能多的内容"）；
//      而 `UMango.Core.Utils.Extensions.Between` 是「首次出现 → 其后首次出现」。
//      两者语义不同故分开存放，各自保留。
//   ③ `RegexSeek(this string, Regex)` 修正了原版**把 RegexOptions 当成
//      startAt 传入** `regex.Match(str, (int)options)` 的缺陷 —— 那会让
//      传入 `RegexOptions.None`(0) 时从索引 0 开始、传
//      `RegexOptions.IgnoreCase`(1) 时从索引 1 开始（丢掉首字符）。
//   ④ `StrFill` 在 `code` 为空串时不再抛 `IndexOutOfRangeException`，
//      改为用空格填充。
//   ⑤ `Val` 不再吞掉所有异常后返回 0 却不区分「输入为 0」与「输入非法」，
//      额外提供 `TryVal` 明确返回是否解析成功。
//
// 文件内不引用任何 Windows 专属 API，可在 net10.0 支编译。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UMango.Core.Text;

/// <summary>
/// 字符串处理扩展方法集。
/// </summary>
public static class TextExtensions
{
    // ================= 基础 =================

    /// <summary>首字母大写，其余小写。</summary>
    public static string Capitalize(this string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }

    /// <summary>
    /// 将字符串统一到某个长度：过短则用 <paramref name="code"/> 的首字符在左侧填充，
    /// 过长则截取靠左的指定长度。
    /// </summary>
    public static string StrFill(this string str, string code, byte length)
    {
        if (str is null) throw new ArgumentNullException(nameof(str));
        if (str.Length > length)
            return str[..length];
        var filler = string.IsNullOrEmpty(code) ? ' ' : code[0];
        return str.PadLeft(length, filler);
    }

    /// <summary>
    /// 将数字格式化为固定的小数位数（向零取整，四舍五入由格式化器决定）。
    /// 例：12 → "12.00"，95.678 → "95.68"。
    /// </summary>
    public static string StrFillNum(this double num, int length)
        => num.ToString("F" + length.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// 移除字符串首尾的标点符号、回车，以及括号中、冒号后的补充说明内容。
    /// </summary>
    public static string StrTrim(this string str, bool removeQuote = true)
    {
        if (str is null) throw new ArgumentNullException(nameof(str));
        if (removeQuote)
            str = str.Split("（")[0].Split("：")[0].Split("(")[0].Split(":")[0];
        return str.Trim('.', '。', '！', ' ', '!', '?', '？', '\r', '\n');
    }

    /// <summary>用分隔符连接集合元素（null 元素按空串处理）。</summary>
    public static string Join(this IEnumerable list, string split)
    {
        if (list is null) throw new ArgumentNullException(nameof(list));
        var builder = new StringBuilder();
        var isFirst = true;
        foreach (var element in list)
        {
            if (isFirst) isFirst = false;
            else builder.Append(split);
            if (element is not null) builder.Append(element);
        }

        return builder.ToString();
    }

    /// <summary>按分隔符分割（单个字符走 char 快路径）。</summary>
    public static string[] Split(this string fullStr, string splitStr)
    {
        if (fullStr is null) throw new ArgumentNullException(nameof(fullStr));
        if (splitStr is null) throw new ArgumentNullException(nameof(splitStr));
        if (splitStr.Length == 1) return fullStr.Split(splitStr[0]);
        return fullStr.Split([splitStr], StringSplitOptions.None);
    }

    /// <summary>djb2 变体字符串哈希（与 PCL 的 GetHash 同算法，仅用于非安全用途）。</summary>
    public static ulong GetHash(this string str)
    {
        if (str is null) throw new ArgumentNullException(nameof(str));
        ulong hash = 5381;
        foreach (var c in str)
            hash = (hash << 5) ^ hash ^ c;
        return hash ^ 0xA98F501BC684032FUL;
    }

    /// <summary>检查字符串中的字符是否均为 ASCII 字符。</summary>
    public static bool IsAscii(this string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        foreach (var c in input)
            if (c >= 128)
                return false;
        return true;
    }

    // ================= 查找 / 截取 =================

    /// <summary>获取子字符串第一次出现之前的部分；未找到则原样返回。</summary>
    public static string BeforeFirst(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.IndexOfF(text, ignoreCase);
        return pos >= 0 ? str[..pos] : str;
    }

    /// <summary>获取子字符串最后一次出现之前的部分；未找到则原样返回。</summary>
    public static string BeforeLast(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.LastIndexOfF(text, ignoreCase);
        return pos >= 0 ? str[..pos] : str;
    }

    /// <summary>获取子字符串第一次出现之后的部分；未找到则原样返回。</summary>
    public static string AfterFirst(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.IndexOfF(text, ignoreCase);
        return pos >= 0 ? str[(pos + text.Length)..] : str;
    }

    /// <summary>获取子字符串最后一次出现之后的部分；未找到则原样返回。</summary>
    public static string AfterLast(this string str, string text, bool ignoreCase = false)
    {
        var pos = string.IsNullOrEmpty(text) ? -1 : str.LastIndexOfF(text, ignoreCase);
        return pos >= 0 ? str[(pos + text.Length)..] : str;
    }

    /// <summary>
    /// 获取处于两个子字符串之间的部分：<paramref name="after"/> 取**最后一次**出现，
    /// <paramref name="before"/> 取其后**第一次**出现。任一未找到则退化为不裁切。
    /// </summary>
    public static string Between(this string str, string after, string before, bool ignoreCase = false)
    {
        var startPos = string.IsNullOrEmpty(after) ? -1 : str.LastIndexOfF(after, ignoreCase);
        if (startPos >= 0) startPos += after.Length;
        else startPos = 0;

        var endPos = string.IsNullOrEmpty(before) ? -1 : str.IndexOfF(before, startPos, ignoreCase);
        if (endPos >= 0) return str.Substring(startPos, endPos - startPos);
        if (startPos > 0) return str[startPos..];
        return str;
    }

    // ================= 高速比较 =================

    /// <summary>可选择忽略大小写的 StartsWith。</summary>
    public static bool StartsWithF(this string str, string prefix, bool ignoreCase = false)
        => str.StartsWith(prefix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>可选择忽略大小写的 EndsWith。</summary>
    public static bool EndsWithF(this string str, string suffix, bool ignoreCase = false)
        => str.EndsWith(suffix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>可选择忽略大小写的 Contains。</summary>
    public static bool ContainsF(this string str, string subStr, bool ignoreCase = false)
        => str.IndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;

    /// <summary>可选择忽略大小写的 IndexOf。</summary>
    public static int IndexOfF(this string str, string subStr, bool ignoreCase = false)
        => str.IndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>可选择忽略大小写的 IndexOf（带起始位置）。</summary>
    public static int IndexOfF(this string str, string subStr, int startIndex, bool ignoreCase = false)
        => str.IndexOf(subStr, startIndex, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>可选择忽略大小写的 LastIndexOf。</summary>
    public static int LastIndexOfF(this string str, string subStr, bool ignoreCase = false)
        => str.LastIndexOf(subStr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>可选择忽略大小写的 LastIndexOf（带起始位置）。</summary>
    public static int LastIndexOfF(this string str, string subStr, int startIndex, bool ignoreCase = false)
        => str.LastIndexOf(subStr, startIndex, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ================= 数值 =================

    /// <summary>
    /// 宽松解析为 double；解析失败返回 0（与 PCL 的 Val 同语义）。
    /// 需要区分「输入就是 0」与「输入非法」时请用 <see cref="TryVal"/>。
    /// </summary>
    public static double Val(this string str)
        => str.TryVal(out var value) ? value : 0d;

    /// <summary>宽松解析为 double，返回是否成功。</summary>
    public static bool TryVal(this string? str, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(str)) return false;
        return double.TryParse(str, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    // ================= 转义 =================

    /// <summary>XML 转义（含 PCL 的「以 { 开头则前置 {}」的特殊处理）。</summary>
    public static string EscapeXml(this string str)
    {
        if (str is null) throw new ArgumentNullException(nameof(str));
        if (str.StartsWithF("{"))
            str = "{}" + str;
        return str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("'", "&apos;").Replace("\"", "&quot;").Replace("\r\n", "&#xa;");
    }

    /// <summary>转义 SQL LIKE 模式中的通配符。</summary>
    public static string EscapeLikePattern(this string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        var sb = new StringBuilder();
        foreach (var c in input)
            switch (c)
            {
                case '[':
                case ']':
                case '*':
                case '?':
                case '#':
                    sb.Append('[').Append(c).Append(']');
                    break;
                default:
                    sb.Append(c);
                    break;
            }

        return sb.ToString();
    }

    // ================= 正则 =================

    /// <summary>搜索字符串中的所有正则匹配项（正则非法时返回空表，不抛异常）。</summary>
    public static List<string> RegexSearch(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        var result = new List<string>();
        if (str is null || regex is null) return result;
        try
        {
            foreach (Match item in new Regex(regex, options).Matches(str))
                result.Add(item.Value);
        }
        catch (ArgumentException)
        {
            return new List<string>();
        }

        return result;
    }

    /// <summary>搜索字符串中的所有正则匹配项（正则非法时返回空表，不抛异常）。</summary>
    public static List<string> RegexSearch(this string str, Regex regex)
    {
        var result = new List<string>();
        if (str is null || regex is null) return result;
        try
        {
            foreach (Match item in regex.Matches(str))
                result.Add(item.Value);
        }
        catch (ArgumentException)
        {
            return new List<string>();
        }

        return result;
    }

    /// <summary>获取第一个正则匹配项；无匹配或正则非法时返回 null。</summary>
    public static string? RegexSeek(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        if (str is null || regex is null) return null;
        try
        {
            var result = Regex.Match(str, regex, options).Value;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 获取第一个正则匹配项；无匹配或正则非法时返回 null。
    /// ⚠️ 修正了 PCL 原版把 <see cref="RegexOptions"/> 当成 startAt 传入的缺陷。
    /// </summary>
    public static string? RegexSeek(this string str, Regex regex)
    {
        if (str is null || regex is null) return null;
        try
        {
            var result = regex.Match(str).Value;
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>检查字符串是否匹配某正则模式（正则非法时返回 false）。</summary>
    public static bool RegexCheck(this string str, string regex, RegexOptions options = RegexOptions.None)
    {
        if (str is null || regex is null) return false;
        try
        {
            return Regex.IsMatch(str, regex, options);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>正则替换（正则非法时抛异常，调用方需自行处理）。</summary>
    public static string RegexReplace(this string allContents, string searchRegex, string replaceTo,
        RegexOptions options = RegexOptions.None)
        => Regex.Replace(allContents, searchRegex, replaceTo, options);

    /// <summary>对每个正则匹配分别进行替换。</summary>
    public static string RegexReplaceEach(this string allContents, string searchRegex, MatchEvaluator replaceTo,
        RegexOptions options = RegexOptions.None)
        => Regex.Replace(allContents, searchRegex, replaceTo, options);
}
