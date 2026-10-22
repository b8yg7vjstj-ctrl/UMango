using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using UMango.Core.Configuration;
using UMango.App.Services;
using UMango.Core.Localization;
using UMango.Core.Logging;

namespace UMango.App.Localization;

/// <summary>
/// App 层的本地化接线：把 <see cref="LocalizationService"/>（Core，平台中立）
/// 与 WPF 的资源字典机制接起来。
///
/// **思路来源**：PCL-CE <c>LocalizationService._ApplyLanguageResourcesCore</c>（Apache-2.0）：
/// 兜底层字典（默认语言）始终加载，当前语言字典叠加在其后 —— 后加载的同名键
/// 在 <see cref="ResourceDictionary"/> 查找中优先，因此非默认语言只需写差异键。
/// XAML 侧必须用 <c>{DynamicResource 键}</c> 才能在运行时切换后自动刷新。
/// </summary>
public static class AppLocalization
{
    // ⚠ 路径必须**小写**：WPF 编译时把资源名规范化成小写
//    （实测 UMango.g.resources 里是 localization/languages/zh-cn.baml），
//    而 pack URI 在部分宿主（pack://application:,,,）下对大小写并不总是宽容。
//    这里与资源名严格一致，不赌行为。
private const string DictPrefix = "pack://application:,,,/umango.app;component/localization/languages/";

    private static ResourceDictionary? _fallbackDict;
    private static ResourceDictionary? _currentDict;

    /// <summary>设置项键：UI 语言。</summary>
    public const string LanguageSettingKey = "ui.language";

    /// <summary>设置项键：展示格式区域性。</summary>
    public const string FormatCultureSettingKey = "ui.formatCulture";

    /// <summary>
    /// 启动时调用：按已保存的设置应用语言。
    /// 必须在主窗口创建**之前**调用，否则首帧会用默认语言画一遍再跳。
    /// </summary>
    public static void ApplySavedLanguage()
    {
        var language = ReadSetting(LanguageSettingKey);
        var format = ReadSetting(FormatCultureSettingKey);

        var changed = LocalizationService.Apply(language, format);
        if (changed)
        {
            ApplyDictionaries(LocalizationService.CurrentLanguage.Code);
            Log.Info("Localization", $"已应用语言：{LocalizationService.CurrentLanguage.Code}，" +
                                    $"展示格式：{LocalizationService.CurrentFormatCulture.Name}");
        }
        else
        {
            // 即使语言没变，也要保证字典在位（例：启动时资源里还没有兜底层）
            ApplyDictionaries(LocalizationService.CurrentLanguage.Code);
        }
    }

    /// <summary>
    /// 切换语言（供设置页调用）。会持久化并刷新界面。
    /// </summary>
    /// <param name="code">语言代码，<see cref="LocalizationModels.Auto"/> = 跟随系统。</param>
    public static void SetLanguage(string code)
    {
        var normalized = LocalizationModels.NormalizeCode(code);
        WriteSetting(LanguageSettingKey, normalized);

        LocalizationService.Apply(normalized, LocalizationModels.FormatCultureFollowLanguage);
        ApplyDictionaries(LocalizationService.CurrentLanguage.Code);

        // 字典已在位，现在通知代码侧的句子重建
        LocalizedTextRefreshRequested?.Invoke();

        Log.Info("Localization", $"语言已切换为 {LocalizationService.CurrentLanguage.Code}");
    }

    /// <summary>
    /// 语言切换后触发（在资源字典已重建之后）。
    ///
    /// 订阅方用于刷新**代码侧拼的本地化句子**：XAML 里的 {DynamicResource} 会随
    /// 字典替换自动更新，但 ViewModel 里 string.Format 出来的字符串不会，
    /// 必须显式重建（见 HomeViewModel.RefreshLocalizedText）。
    /// </summary>
    public static event Action? LocalizedTextRefreshRequested;

    /// <summary>
    /// 重建资源字典栈：先移除旧的，再加兜底层，最后（非默认语言时）加叠加层。
    /// 必须在 UI 线程调用（<see cref="Application.Resources"/> 是 DispatcherObject）。
    /// </summary>
    private static void ApplyDictionaries(string languageCode)
    {
        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;

        if (_fallbackDict is not null) dicts.Remove(_fallbackDict);
        if (_currentDict is not null) dicts.Remove(_currentDict);

        // ① 兜底层：默认语言，始终加载
        _fallbackDict = LoadDictionary(LocalizationModels.DefaultLanguageCode);
        if (_fallbackDict is not null) dicts.Add(_fallbackDict);

        // ② 叠加层：仅当不是默认语言
        _currentDict = null;
        if (!LocalizationModels.IsDefaultLanguage(languageCode))
        {
            _currentDict = LoadDictionary(languageCode);
            if (_currentDict is not null) dicts.Add(_currentDict);
        }
        PublishTextProvider();
    }

    private static ResourceDictionary? LoadDictionary(string languageCode)
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri($"{DictPrefix}{languageCode}.xaml", UriKind.Absolute)
            };
        }
        catch (Exception ex)
        {
            // 语言文件缺失/损坏不应把启动挡掉：退回只用默认语言
            Log.Warn("Localization", $"加载语言 {languageCode} 失败，将使用默认语言：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读设置（语言）。设置服务是 <see cref="Services.AppServices.Settings"/>；
    /// 读不到/抛错时返回 null（调用方按"未设置"处理，等于跟随系统）。
    /// </summary>
    private static string? ReadSetting(string key)
    {
        try
        {
            return AppSettings.Settings.Get<string>(key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteSetting(string key, string value)
    {
        try
        {
            AppSettings.Settings.Set(key, value);
        }
        catch (Exception ex)
        {
            Log.Warn("Localization", $"保存语言设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读已保存的语言代码（供设置页回显选中项）。
    /// 未保存过时返回 <see cref="LocalizationModels.Auto"/>。
    /// </summary>
    public static string ReadSavedLanguageCode()
    {
        var saved = ReadSetting(LanguageSettingKey);
        return LocalizationSetting.NormalizeSetting(saved);
    }

    /// <summary>
    /// 当前语言下取文本（供 code-behind 用）。找不到键时返回键本身，
    /// 与 <c>PCL.Lang</c> 的既有行为一致（调试构建返回 <c>!key!</c>）。
    /// </summary>
    public static string Text(string key)
    {
        // ⚠ Application.Current 可能为 null（无 UI 环境：单元测试宿主、控制台模式）。
        //   此时**不能**去构造 Application —— 那会拖入 PresentationFramework，
        //   而它只存在于 Windows，macOS 上直接 FileNotFoundException
        //   （App.Tests 里 7 个 SettingsViewModel 用例就是这样全军覆没的）。
        if (Application.Current is not { } app) return key;
        if (app.TryFindResource(key) is string text) return text;
        return key;
    }

    /// <summary>
    /// 「跟随系统」这一项的显示名。刻意**不读资源字典**：见
    /// <see cref="BuildOptions"/> 的说明（避免在无 UI 环境碰 Application 类型）。
    /// </summary>
    private static string FollowSystemDisplayName() =>
        System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } ui
            ? $"{LocalizationModels.Auto}（{ui}）"
            : LocalizationModels.Auto;

    /// <summary>供设置页下拉框用的语言选项（首项 = 跟随系统）。</summary>
    public static IReadOnlyList<LanguageOption> BuildOptions()
    {
        // ⚠ 首项文案**不要**调 <see cref="Text"/>：那会碰 Application 类型，
        //   而 PresentationFramework 只存在于 Windows，macOS 上测试宿主直接
        // FileNotFoundException（7 个 SettingsViewModel 用例就是这样全军覆没的）。
        // 「跟随系统」这句话本身就该跟着界面语言走，而 DisplayName 恰好就是
        // 各语言自称，语义等价且零依赖。
        var options = new List<LanguageOption>
        {
            new(LocalizationModels.Auto, FollowSystemDisplayName())
        };

        foreach (var language in LocalizationModels.SupportedLanguages)
            options.Add(new LanguageOption(language.Code, language.DisplayName));

        return options;
    }

    /// <summary>设置页下拉框的一项。</summary>
    public sealed record LanguageOption(string Code, string DisplayName);

    /// <summary>
    /// 把 WPF 资源字典包装成 <see cref="ITextProvider"/>，装进
    /// <see cref="LocalizationService"/>。这样业务层/ViewModel 就能在**不引用
    /// Application 类型**的前提下取本地化文案 —— 那个类型在 macOS 测试宿主里
    /// 会让 PresentationFramework 加载失败（App.Tests 56 处构造 ViewModel）。
    /// </summary>
    private sealed class ResourceDictionaryTextProvider : ITextProvider
    {
        private readonly Func<string, string?> _lookup;

        public ResourceDictionaryTextProvider(Func<string, string?> lookup) => _lookup = lookup;

        public string Text(string key) => _lookup(key) ?? key;
    }

    /// <summary>
    /// 让 <see cref="LocalizationService.Text"/> 走本类的资源字典查找。
    /// 在 <see cref="ApplyDictionaries"/> 之后调用（字典已就位）。
    /// </summary>
    private static void PublishTextProvider()
    {
        LocalizationService.SetTextProvider(
            new ResourceDictionaryTextProvider(key => Application.Current?.TryFindResource(key) as string));
    }
}

/// <summary>
/// 语言设置的工具方法（与 <see cref="AppLocalization"/> 分开，便于测试时只依赖 Core）。
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
