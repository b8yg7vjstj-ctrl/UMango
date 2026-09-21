namespace UMango.Core.Utils;

/// <summary>
/// 通用扩展方法集。
///
/// 对应 PCL-CE 的 Utils/Exts/（8 文件，Apache-2.0）。
/// 提供常用字符串/集合/任务/类型测试扩展方法，
/// 让调用侧代码更简洁（这是"降低重复代码率"的一部分）。
/// </summary>
public static class Extensions
{
    // ================= 字符串 =================

    /// <summary>是否为 null 或空/空白。</summary>
    public static bool IsBlank(this string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>截断字符串到最大长度，超出部分加"…"。</summary>
    public static string Truncate(this string s, int maxLen, string suffix = "…")
    {
        if (s.Length <= maxLen) return s;
        return s[..maxLen] + suffix;
    }

    /// <summary>安全取子串：越界不抛异常，返回原始字符串。</summary>
    public static string SafeSubstring(this string s, int start, int length)
    {
        if (start >= s.Length) return "";
        return s.Substring(start, Math.Min(length, s.Length - start));
    }

    /// <summary>在 source 中查找 start 与 end 之间的子串。找不到返回 null。</summary>
    public static string? Between(this string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = source.IndexOf(end, i, StringComparison.Ordinal);
        if (j < 0) return null;
        return source[i..j];
    }

    /// <summary>在 source 中查找 start 之后的第一个匹配位置之后的子串到 end。</summary>
    public static string? AfterFirst(this string source, string marker)
    {
        var i = source.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : source[(i + marker.Length)..];
    }

    /// <summary>在 source 中查找 end 之前的最后一个匹配位置之前的子串。</summary>
    public static string? BeforeLast(this string source, string marker)
    {
        var i = source.LastIndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : source[..i];
    }

    // ================= 集合 =================

    /// <summary>安全取第 n 个元素，越界返回 default。</summary>
    public static T? AtOrDefault<T>(this IList<T> list, int index)
    {
        if (index < 0 || index >= list.Count) return default;
        return list[index];
    }

    /// <summary>把集合转成 HashSet（快速去重）。</summary>
    public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T>? comparer = null)
    {
        return comparer is null
            ? new HashSet<T>(source)
            : new HashSet<T>(source, comparer);
    }

    // ================= 任务 =================

    /// <summary>忽略 Task 的结果，只等完成（用于 fire-and-forget）。</summary>
    public static async Task IgnoreResult(this Task task, Task? onSuccess = null, Task? onFail = null)
    {
        try { await task; } catch { }
    }

    /// <summary>WithTimeout：给 Task 加超时。</summary>
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            return await task;
        throw new TimeoutException($"操作超时（{timeout.TotalSeconds:F0} 秒）");
    }
}
