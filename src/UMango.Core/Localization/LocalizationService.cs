using System;
using System.Collections.Generic;
using System.Globalization;
using UMango.Core.Logging;

namespace UMango.Core.Localization;

/// <summary>
/// 本地化的**平台中立核心**：当前语言/展示区域性、按代码解析、语言切换的判定。
///
/// **思路来源**：PCL-CE <c>LocalizationService</c>（Apache-2.0），保留其
/// 「auto / 默认语言兜底 / UI 语言与展示格式分离」三件事；
/// 但把「加载并替换 WPF 资源字典」留在 App 层（那是 WPF 专有能力），
/// 因此本类可在无 UI 的环境下被单元测试。
///
/// 分层模型（与 PCL 一致）：
///   ① 兜底层 = 默认语言（zh-CN）资源，**始终加载**；
///   ② 叠加层 = 当前语言资源，仅当不是默认语言时加载，键与兜底层重复时覆盖。
/// 好处：非默认语言只需翻译"变了的键"，不必整包复制。
/// </summary>
public static class LocalizationService
{
    private static readonly object Gate = new();

    private static LocalizationLanguage _current = LocalizationModels.ResolveLanguage(LocalizationModels.Auto);
    private static CultureInfo _formatCulture = CultureInfo.CurrentCulture;

    /// <summary>
    /// 文案查找提供器。默认是 <see cref="KeyOnlyTextProvider"/>（无 UI 环境直接返回键）；
    /// App 层启动时把它换成资源字典实现，于是一切 <c>Text(key)</c> 都走本地化文案。
    /// 测试可以直接换成一个查表实现来断言文案键是否正确。
    /// </summary>
    private static ITextProvider _text = KeyOnlyTextProvider.Instance;

    /// <summary>替换文案查找提供器（App 启动时调用；测试可随时替换）。</summary>
    /// <summary>
    /// 当前生效的文案提供器（供测试捕获后还原）。
    /// 只读：替换一律走 <see cref="SetTextProvider"/>（那里做 null 回落）。
    /// </summary>
    public static ITextProvider CurrentProvider
    {
        get { lock (Gate) { return _text; } }
    }

    public static void SetTextProvider(ITextProvider provider)
    {
        lock (Gate) { _text = provider ?? KeyOnlyTextProvider.Instance; }
    }

    /// <summary>
    /// 取当前语言下的文案。**不会抛异常**：键不存在时返回键本身，
    /// 因此业务代码可以放心在构造函数里调用（不要求资源字典已就绪）。
    /// </summary>
    public static string Text(string key)
    {
        lock (Gate) { return _text.Text(key); }
    }

    /// <summary>
    /// 取文案并按 <paramref name="args"/> 格式化（等价 <c>string.Format(Text(key), args)</c>）。
    /// 用于「未检测到 {0} 的命令行工具」这类带占位符的句子。
    ///
    /// <para>
    /// **不会抛异常** —— 与 <see cref="Text(string)"/> 同一个契约。
    /// 模板占位符个数与实参个数对不上时（比如文案改了但调用点没跟着改），
    /// 这里不让 <see cref="FormatException"/> 把整个应用带崩，而是补齐空位、
    /// 记一条警告日志，然后返回可读的句子。
    /// </para>
    ///
    /// <para>
    /// 这条不是纸面承诺：曾经就有一个真实缺陷 —— 诊断页把
    /// <c>Tools.Report.RecordCount</c>（3 个占位符）当单参调用，
    /// 导致 Avalonia 壳在 <b>启动即崩溃</b>（构造函数里取文案）。
    /// </para>
    /// </summary>
    public static string Text(string key, params object?[] args)
    {
        var template = Text(key);
        if (args is null || args.Length == 0) return template;
        return SafeFormat(key, template, args);
    }

    /// <summary>
    /// 安全的格式化：参数个数对不上时不抛异常。
    /// </summary>
    private static string SafeFormat(string key, string template, object?[] args)
    {
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException ex)
        {
            Log.Warn("Localization",
                $"文案键「{key}」的占位符与实参个数不匹配：{ex.Message}；模板=「{template}」，实参={args.Length} 个");
        }

        // 兜底一：把缺失的实参补成空串再试一次（模板本身合法时就能得到可读结果）
        var needed = MaxPlaceholderIndex(template) + 1;
        if (needed > args.Length)
        {
            var padded = new object?[needed];
            Array.Copy(args, padded, args.Length);
            for (var i = args.Length; i < needed; i++) padded[i] = string.Empty;
            try
            {
                return string.Format(template, padded);
            }
            catch (FormatException)
            {
                // 模板本身还有别的问题（格式说明符非法等）→ 走兜底二
            }
        }

        // 兜底二：把占位符整段去掉，至少返回一句没有花括号的文字。
        return PlaceholderPattern.Replace(template, string.Empty);
    }

    /// <summary>匹配 <c>{数字}</c> 形式的占位符（跳过 <c>{{</c> 转义）。</summary>
    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"(?<!\{)\{\d+[^}]*\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>匹配 <c>{数字}</c> 并抓出索引（跳过 <c>{{</c> 转义）。</summary>
    private static readonly System.Text.RegularExpressions.Regex PlaceholderIndexPattern =
        new(@"(?<!\{)\{(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>模板里最大的占位符索引；没有占位符时返回 -1。</summary>
    private static int MaxPlaceholderIndex(string template)
    {
        var max = -1;
        foreach (System.Text.RegularExpressions.Match m in PlaceholderIndexPattern.Matches(template))
        {
            if (int.TryParse(m.Groups[1].Value, out var index) && index > max) max = index;
        }
        return max;
    }

    /// <summary>当前 UI 语言。</summary>
    public static LocalizationLanguage CurrentLanguage
    {
        get { lock (Gate) { return _current; } }
    }

    /// <summary>当前展示格式所使用的区域性（数字/日期/时间的显示格式）。</summary>
    public static CultureInfo CurrentFormatCulture
    {
        get { lock (Gate) { return _formatCulture; } }
    }

    /// <summary>语言或展示格式变化后触发（订阅方负责刷新界面）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// 应用 UI 语言。
    /// </summary>
    /// <param name="code">语言代码，<see cref="LocalizationModels.Auto"/> 表示跟随系统。</param>
    /// <param name="formatCode">
    /// 展示格式区域性；<see cref="LocalizationModels.FormatCultureFollowLanguage"/> 表示跟随 UI 语言；
    /// <see cref="LocalizationModels.Auto"/> 表示跟随系统。
    /// </param>
    /// <returns>是否真的发生了变化（无变化时不必触发刷新）。</returns>
    public static bool Apply(string? code, string? formatCode = LocalizationModels.FormatCultureFollowLanguage)
    {
        var language = LocalizationModels.ResolveLanguage(code);
        var format = ResolveFormatCulture(formatCode, language);

        bool changed;
        lock (Gate)
        {
            changed = !string.Equals(_current.Code, language.Code, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(_formatCulture.Name, format.Name, StringComparison.OrdinalIgnoreCase);
            if (!changed) return false;

            _current = language;
            _formatCulture = format;
        }

        // 线程的 UI 区域性也跟着切：string.Format 之类的默认格式化会跟随它
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language.CultureName);
        CultureInfo.CurrentCulture = format;

        LanguageChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 解析展示格式区域性。
    /// </summary>
    public static CultureInfo ResolveFormatCulture(string? code, LocalizationLanguage? language = null)
    {
        language ??= CurrentLanguage;
        var normalized = LocalizationModels.NormalizeCode(code);

        if (string.Equals(normalized, LocalizationModels.FormatCultureFollowLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return SafeGetCulture(language.CultureName);
        }

        if (string.Equals(normalized, LocalizationModels.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.CurrentCulture;
        }

        return SafeGetCulture(normalized);
    }

    /// <summary>语言代码是否受支持。</summary>
    public static bool IsLanguageSupported(string? code) => LocalizationModels.IsLanguageSupported(code);

    /// <summary>
    /// 取区域性；取不到时回落不变区域性，避免把启动/切换挡掉。
    ///
    /// ⚠ 不能只靠 catch <see cref="CultureNotFoundException"/>：实测 macOS（ICU 后端）上
    /// <c>GetCultureInfo("not-a-culture")</c> **不抛异常**，而是返回一个 Name="not" 的
    /// 中性文化；只有 Windows 后端才抛异常。因此必须额外校验返回值的 Name 与请求一致，
    /// 否则无效区域性会静默渗透到 CurrentFormatCulture 里（日期/数字格式化随之跑偏）。
    /// 这条是实测发现的，见 LocalizationServiceTests 的对应用例。
    /// </summary>
    private static CultureInfo SafeGetCulture(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return CultureInfo.InvariantCulture;

        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            // 归一化后比对：GetCultureInfo 会规范大小写，但不会改结构
            if (string.Equals(culture.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return culture;

            return CultureInfo.InvariantCulture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
