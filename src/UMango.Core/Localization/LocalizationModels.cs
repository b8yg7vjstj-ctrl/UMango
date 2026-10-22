using System.Collections.Generic;

namespace UMango.Core.Localization;

/// <summary>
/// 一种受支持的 UI 语言。
/// </summary>
/// <param name="Code">语言代码（BCP-47，如 <c>zh-CN</c>）。</param>
/// <param name="DisplayName">用该语言自身书写、给用户看的名字（语言选择列表用它）。</param>
/// <param name="CultureName">对应的 <see cref="System.Globalization.CultureInfo"/> 名。</param>
public sealed record LocalizationLanguage(string Code, string DisplayName, string CultureName);

/// <summary>
/// 本地化的常量与语言清单。
///
/// **思路来源**：PCL-CE <c>PCL.Core/App/Localization/LocalizationService.cs</c>（Apache-2.0）。
/// UMango 保留其三个核心设计：
///   ① <see cref="Auto"/> = 跟随系统语言；
///   ② <see cref="DefaultLanguageCode"/> 既是默认语言也是**完整兜底**（资源字典分层叠加）；
///   ③ UI 语言与「展示格式区域性」分离（数字/日期格式可单独设置）。
/// </summary>
public static class LocalizationModels
{
    /// <summary>跟随系统语言。</summary>
    public const string Auto = "auto";

    /// <summary>展示格式跟随 UI 语言。</summary>
    public const string FormatCultureFollowLanguage = "ui-language";

    /// <summary>默认语言，也是语言资源的完整兜底。</summary>
    public const string DefaultLanguageCode = "zh-CN";

    /// <summary>
    /// 受支持的 UI 语言。
    ///
    /// 与 PCL-CE 的清单对齐（PCL 支持 7 种，其中 3 个是空壳）；
    /// UMango 先提供**有实际资源**的几种，避免列出选完还是默认语言的假选项。
    /// </summary>
    public static IReadOnlyList<LocalizationLanguage> SupportedLanguages { get; } =
    [
        new(DefaultLanguageCode, "简体中文（中国大陆）", "zh-CN"),
        new("en-US", "English (US)", "en-US"),
    ];

    /// <summary>当前语言是否为默认语言（默认语言不叠加额外字典，靠兜底层）。</summary>
    public static bool IsDefaultLanguage(string? code) =>
        string.Equals(NormalizeCode(code), DefaultLanguageCode, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 归一化语言代码：<c>_</c> → <c>-</c>、去空白；空值返回 <see cref="Auto"/>。
    /// </summary>
    public static string NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? Auto : code.Replace('_', '-').Trim();

    /// <summary>判断语言代码是否受支持（空值视为支持：表示 auto）。</summary>
    public static bool IsLanguageSupported(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return true;
        if (string.Equals(code, Auto, System.StringComparison.OrdinalIgnoreCase)) return true;
        var normalized = NormalizeCode(code);
        foreach (var language in SupportedLanguages)
        {
            if (string.Equals(language.Code, normalized, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 按代码解析语言：auto → 跟随系统；不受支持 → 回落默认语言。
    /// </summary>
    public static LocalizationLanguage ResolveLanguage(string? code)
    {
        var normalized = NormalizeCode(code);
        if (string.Equals(normalized, Auto, System.StringComparison.OrdinalIgnoreCase))
            return ResolveSystemLanguage();

        foreach (var language in SupportedLanguages)
        {
            if (string.Equals(language.Code, normalized, System.StringComparison.OrdinalIgnoreCase))
                return language;
        }

        return SupportedLanguages[0];
    }

    /// <summary>
    /// 跟随系统语言：匹配系统 UI 区域性的语言部分（<c>zh-TW</c> 命中 <c>zh-TW</c>；
    /// <c>zh-HK</c> 之类未列的语言回落默认）。
    /// </summary>
    public static LocalizationLanguage ResolveSystemLanguage()
    {
        var ui = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(ui)) return SupportedLanguages[0];

        foreach (var language in SupportedLanguages)
        {
            if (string.Equals(language.Code, ui, System.StringComparison.OrdinalIgnoreCase))
                return language;
        }

        // 语言部分匹配（例：zh-SG → zh-CN）
        var lang = ui.Split('-')[0];
        foreach (var language in SupportedLanguages)
        {
            if (language.Code.StartsWith(lang, System.StringComparison.OrdinalIgnoreCase))
                return language;
        }

        return SupportedLanguages[0];
    }
}
