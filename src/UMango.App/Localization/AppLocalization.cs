using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using UMango.Core.Configuration;
using UMango.App.Services;
using UMango.Core.Localization;
using UMango.Core.Logging;
using UMango.Shell.Localization;
using UMango.Shell.Services;

namespace UMango.App.Localization;

/// <summary>
/// App 层的本地化接线：把 <see cref="UMango.Core.Localization.LocalizationService"/>（Core，平台中立）
/// 与 WPF 的资源字典机制接起来。
///
/// <para>
/// <b>文案来自哪</b>：<c>UMango.Shell</c> 内嵌的纯文本语言文件（<c>Localization/Languages/*.txt</c>）——
/// 那是**唯一真相源**，Avalonia 壳读的是同一份内嵌资源。WPF 侧的 <c>.xaml</c> 资源字典
/// 是由 <c>scripts/sync-language-xaml.py</c> 从 <c>.txt</c> 生成的**派生产物**，
/// 启动时不再从 pack URI 读它（那会与真相源分叉）。
/// </para>
///
/// <para>
/// <b>思路来源</b>：PCL-CE <c>LocalizationService._ApplyLanguageResourcesCore</c>（Apache-2.0）：
/// 兜底层字典（默认语言）始终加载，当前语言字典叠加在其后 —— 后加载的同名键
/// 在 <see cref="ResourceDictionary"/> 查找中优先，因此非默认语言只需写差异键。
/// XAML 侧必须用 <c>{DynamicResource 键}</c> 才能在运行时切换后自动刷新。
/// </para>
/// </summary>
public static class AppLocalization
{
    /// <summary>设置项键：UI 语言。</summary>
    public const string LanguageSettingKey = "ui.language";

    /// <summary>设置项键：展示格式区域性。</summary>
    public const string FormatCultureSettingKey = "ui.formatCulture";

    /// <summary>当前生效的语言目录（供需要按语言取值的场合使用）。</summary>
    private static LanguageCatalog? _catalog;

    /// <summary>
    /// 上次灌入的语言字典实例。
    ///
    /// **只移除自己那一个** —— 应用级 <see cref="ResourceDictionary.MergedDictionaries"/>
    /// 里还有主题字典（PclTokens / Styles / Controls.Pcl），全清会把主题一起干掉，
    /// 界面会瞬间退回 WPF 默认外观。
    /// </summary>
    private static ResourceDictionary? _languageDictionary;

    /// <summary>当前生效的语言目录；未初始化时为 null。</summary>
    public static LanguageCatalog? CurrentCatalog => _catalog;

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
            Log.Info("Localization", $"已应用语言：{LocalizationService.CurrentLanguage.Code}，" +
                                    $"展示格式：{LocalizationService.CurrentFormatCulture.Name}");
        }

        // 无论语言是否变化，都要把字典灌好（首次启动时字典还不存在）。
        ApplyDictionaries(LocalizationService.CurrentLanguage.Code);
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
    /// 重建资源字典栈：清空旧条目，灌入**当前语言合并后**的文案。
    /// 必须在 UI 线程调用（<see cref="Application.Resources"/> 是 DispatcherObject）。
    ///
    /// <para>
    /// 为什么不按「兜底层 + 叠加层」两层分别灌：合并后灌一次即可 ——
    /// 两层合并的语义与 <see cref="LanguageCatalog"/> 完全一致（叠加层覆盖兜底层），
    /// 而 WPF 的 <see cref="ResourceDictionary"/> 本来就是后者优先。
    /// 少一层 = 少一个可能分叉的地方。
    /// </para>
    /// </summary>
    private static void ApplyDictionaries(string languageCode)
    {
        var app = Application.Current;
        if (app is null) return;

        // ① 读真相源（内嵌纯文本）。失败也不该把启动挡掉 —— 退回「只返回键」。
        LanguageCatalog catalog;
        try
        {
            catalog = LanguageCatalog.Load(languageCode);
        }
        catch (Exception ex)
        {
            Log.Warn("Localization", $"加载语言 {languageCode} 失败，将使用默认语言：{ex.Message}");
            catalog = LanguageCatalog.Load(LanguageCatalog.FallbackLanguageCode);
        }

        _catalog = catalog;

        // ② 灌进 WPF 的资源字典。**只动自己那一个**，别碰主题字典。
        var dicts = app.Resources.MergedDictionaries;
        if (_languageDictionary is not null) dicts.Remove(_languageDictionary);

        var dictionary = new ResourceDictionary();
        foreach (var pair in catalog.AllEntries()) dictionary.Add(pair.Key, pair.Value);
        dicts.Add(dictionary);
        _languageDictionary = dictionary;

        // ③ 让业务层/ViewModel 侧的取文案也走同一份目录。
        PublishTextProvider();
    }

    /// <summary>
    /// 读设置（语言）。设置服务是 <see cref="UMango.Shell.Services.AppServices.Settings"/>；
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

    /// <summary>
    /// 写设置（语言）。写失败只记日志，不抛 —— 语言切换不该让界面崩掉。
    /// </summary>
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
        CultureInfo.CurrentUICulture.Name is { Length: > 0 } ui
            ? $"{LocalizationModels.Auto}（{ui}）"
            : LocalizationModels.Auto;

    /// <summary>供设置页下拉框用的语言选项（首项 = 跟随系统）。</summary>
    public static IReadOnlyList<LanguageOption> BuildOptions()
    {
        // ⚠ 首项文案**不要**调 <see cref="Text"/>：那会碰 Application 类型，
        //   而 PresentationFramework 只存在于 Windows，macOS 上测试宿主直接
        //   FileNotFoundException（7 个 SettingsViewModel 用例就是这样全军覆没的）。
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
    /// 让 <see cref="LocalizationService.Text"/> 走 <see cref="LanguageCatalog"/>。
    /// 这样业务层/ViewModel 就能在**不引用 Application 类型**的前提下取本地化文案 ——
    /// 那个类型在 macOS 测试宿主里会让 PresentationFramework 加载失败
    /// （App.Tests 56 处构造 ViewModel）。
    /// </summary>
    private static void PublishTextProvider()
    {
        var catalog = _catalog;
        if (catalog is null)
        {
            LocalizationService.SetTextProvider(KeyOnlyTextProvider.Instance);
            return;
        }

        LocalizationService.SetTextProvider(new CatalogTextProvider(catalog));
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
