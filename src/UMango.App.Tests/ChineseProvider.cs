// SPDX-License-Identifier: Apache-2.0
using UMango.Core.Localization;

namespace UMango.App.Tests;

/// <summary>
/// 测试用的中文文案提供器：把 <c>zh-CN.xaml</c> 里的键→文案装进
/// <see cref="LocalizationService"/>，让断言能写具体中文（而不是资源键）。
///
/// <para>
/// 与 <c>UMango.Shell.Tests</c> 里那份是**同一个实现的两份拷贝**（测试工程之间
/// 不互相引用）—— 两边都指向 WPF 侧那份权威 <c>zh-CN.xaml</c>。
/// </para>
/// </summary>
internal sealed class ChineseProvider : ITextProvider
{
    private static readonly System.Collections.Generic.Dictionary<string, string> Table =
        new(System.StringComparer.Ordinal);

    private const string Pattern = "<sys:String x:Key=\"" + "([^\"]+)\"" + ">(.*)</sys:String>";

    static ChineseProvider()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "UMango.App", "Localization", "Languages", "zh-CN.xaml");
        if (!System.IO.File.Exists(path)) return;
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, Pattern);
            if (m.Success) Table[m.Groups[1].Value] = m.Groups[2].Value;
        }
    }

    public string Text(string key) => Table.TryGetValue(key, out var v) ? v : key;

    public static ScopeHandle Scope()
    {
        var previous = LocalizationService.CurrentProvider;
        LocalizationService.SetTextProvider(new ChineseProvider());
        return new ScopeHandle(previous);
    }

    /// <summary>
    /// 退出时把 **provider 实例**装回去。
    /// ⚠ 不要包成"调旧 provider 的委托"那种形式：一旦捕获的是
    /// <c>LocalizationService.Text</c> 本身，还原后每次取文本都会
    /// Text → 包装 provider → Text → … 无限递归，直接栈溢出把测试进程炸掉。
    /// </summary>
    internal readonly struct ScopeHandle : System.IDisposable
    {
        private readonly ITextProvider _restore;
        public ScopeHandle(ITextProvider restore) => _restore = restore;
        public void Dispose() => LocalizationService.SetTextProvider(_restore);
    }
}
