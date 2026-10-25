using UMango.Core.Localization;

namespace UMango.Shell.Localization;

/// <summary>
/// 语言设置的工具方法（与 <see cref="ShellLocalization"/> 分开，便于测试时只依赖 Core）。
/// </summary>
public static class LocalizationSetting
{
    /// <summary>
    /// 校验设置页传入的语言代码是否可用；不可用时返回 <see cref="LocalizationModels.Auto"/>。
    /// </summary>
    public static string NormalizeSetting(string? code) =>
        LocalizationService.IsLanguageSupported(code)
            ? LocalizationModels.NormalizeCode(code)
            : LocalizationModels.Auto;
}
