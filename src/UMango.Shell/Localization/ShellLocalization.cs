using System;
using System.Collections.Generic;
using System.Globalization;
using UMango.Core.Configuration;
using UMango.Core.Localization;
using UMango.Core.Logging;
using UMango.Shell.Services;

namespace UMango.Shell.Localization;

/// <summary>
/// 本地化的**平台中立编排层**（两个界面壳共用）。
///
/// <para>
/// 分工：
/// <list type="bullet">
///   <item><c>LanguageCatalog</c>：文案的唯一真相源（内嵌纯文本），解析与查表；</item>
///   <item><c>LocalizationService</c>（Core）：当前语言/展示格式区域的判定与切换；</item>
///   <item><b>本类</b>：把「读设置 → 应用语言 → 发布文案提供器 → 通知界面刷新」串起来。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么不在本类里碰任何 UI 框架类型</b>：WPF 的 <c>Application.Current</c> 会拖入
/// PresentationFramework，它只存在于 Windows —— macOS 的测试宿主碰到就
/// <c>FileNotFoundException</c>。本类因此完全平台中立，WPF 壳额外做的事
/// （把文案灌进 <c>ResourceDictionary</c>）留在 WPF 侧那一层薄适配里。
/// </para>
///
/// <para>
/// <b>思路来源</b>：PCL-CE <c>LocalizationService._ApplyLanguageResourcesCore</c>（Apache-2.0）：
/// 兜底层字典（默认语言）始终加载，当前语言字典叠加在其后 —— 后加载的同名键优先，
/// 因此非默认语言只需写差异键。
/// </para>
/// </summary>
public static class ShellLocalization
{
    /// <summary>设置项键：UI 语言（转发到 Core 的设置目录，保证只注册一次）。</summary>
    public const string LanguageSettingKey = UiSettings.LanguageKey;

    /// <summary>设置项键：展示格式区域性（转发到 Core 的设置目录，保证只注册一次）。</summary>
    public const string FormatCultureSettingKey = UiSettings.FormatCultureKey;

    /// <summary>当前生效的语言目录（供需要按语言取值的场合使用）。</summary>
    public static LanguageCatalog? CurrentCatalog { get; private set; }

    /// <summary>
    /// 语言切换后触发（在文案提供器已换新、且宿主已重建自己的资源字典之后）。
    ///
    /// <para>
    /// 订阅方用于刷新**代码侧拼的本地化句子**：界面里绑定的文案会随资源字典刷新，
    /// 但 ViewModel 里 <c>string.Format</c> 出来的字符串不会，必须显式重建。
    /// </para>
    /// </summary>
    public static event Action? LocalizedTextRefreshRequested
    {
        add => _localizedTextRefreshRequested += value;
        remove => _localizedTextRefreshRequested -= value;
    }

    private static Action? _localizedTextRefreshRequested;

    /// <summary>
    /// 供宿主（界面壳）把自己的资源字典重建逻辑挂上来。
    ///
    /// <para>
    /// 平台中立的这一层只管"语言是什么、文案从哪来"，**不懂任何 UI 框架的资源字典** ——
    /// WPF 的 <c>ResourceDictionary</c> 和 Avalonia 的资源字典是各壳自己的事。
    /// 宿主把重建逻辑挂到这个钩子上，两边就共用同一条切换路径，不会一个换了另一个没换。
    /// </para>
    /// </summary>
    public static Action? HostDictionariesRebuilt
    {
        get => _hostDictionariesRebuilt;
        set => _hostDictionariesRebuilt = value;
    }

    private static Action? _hostDictionariesRebuilt;

    /// <summary>
    /// 清掉所有钩子与订阅（**仅供测试**）。
    ///
    /// <para>
    /// 这两个钩子是进程级静态状态，测试之间若不清理，后一个用例会收到前一个用例
    /// 留下的订阅 → 出现"顺序相关的偶发失败"。这里给一条显式的清理路径，
    /// 免得每个测试类各写一份 <c>-=</c> 而漏掉某一个。
    /// </para>
    /// </summary>
    public static void ResetHooks()
    {
        _localizedTextRefreshRequested = null;
        _hostDictionariesRebuilt = null;
        SettingsReader = null;
        SettingsWriter = null;
    }

    /// <summary>
    /// 设置写入的**可替换实现**（**仅供测试**）。见 <see cref="SettingsReader"/> 的说明。
    /// </summary>
    public static Action<string, string>? SettingsWriter { get; set; }


    /// <summary>
    /// 启动时调用：按已保存的设置应用语言。
    ///
    /// <para>
    /// 必须在主窗口创建**之前**调用，否则首帧会用默认语言画一遍再跳。
    /// </para>
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

        // 无论语言是否变化，都要把目录灌好（首次启动时目录还不存在）。
        PublishCatalog(LocalizationService.CurrentLanguage.Code);

        // 让宿主重建自己的资源字典（WPF 的 ResourceDictionary / Avalonia 的资源）。
        HostDictionariesRebuilt?.Invoke();
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
        PublishCatalog(LocalizationService.CurrentLanguage.Code);

        // 宿主先重建资源字典，然后才通知代码侧的句子重建（顺序不能反）。
        HostDictionariesRebuilt?.Invoke();
        _localizedTextRefreshRequested?.Invoke();

        Log.Info("Localization", $"语言已切换为 {LocalizationService.CurrentLanguage.Code}");
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
    /// 取当前语言下的文本。
    ///
    /// <para>
    /// **刻意不碰任何 UI 框架类型**：值来自 <see cref="LanguageCatalog"/>（唯一真相源），
    /// 与界面里显示的文案同源。找不到键时返回键本身。
    /// </para>
    /// </summary>
    public static string Text(string key) => LocalizationService.Text(key);

    /// <summary>
    /// 「跟随系统」这一项的显示名。刻意**不读界面资源**：它跟着界面语言走，
    /// 而 <see cref="LocalizationModels.SupportedLanguages"/> 里每种语言的
    /// <c>DisplayName</c> 恰好就是各语言自称，语义等价且零依赖。
    /// </summary>
    private static string FollowSystemDisplayName() =>
        CultureInfo.CurrentUICulture.Name is { Length: > 0 } ui
            ? $"{LocalizationModels.Auto}（{ui}）"
            : LocalizationModels.Auto;

    /// <summary>供设置页下拉框用的语言选项（首项 = 跟随系统）。</summary>
    public static IReadOnlyList<LanguageOption> BuildOptions()
    {
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
    /// 载入语言目录并发布成 Core 的文案提供器。
    ///
    /// <para>
    /// 载入失败不该把启动挡掉 —— 退回「只返回键」的默认提供器，界面文案退化成键名，
    /// 但应用能用。
    /// </para>
    /// </summary>
    private static void PublishCatalog(string languageCode)
    {
        LanguageCatalog catalog;
        try
        {
            catalog = LanguageCatalog.Load(languageCode);
        }
        catch (Exception ex)
        {
            Log.Warn("Localization", $"加载语言 {languageCode} 失败，将使用默认语言：{ex.Message}");
            try
            {
                catalog = LanguageCatalog.Load(LanguageCatalog.FallbackLanguageCode);
            }
            catch (Exception fallbackEx)
            {
                Log.Error("Localization", $"加载兜底语言也失败：{fallbackEx.Message}");
                LocalizationService.SetTextProvider(KeyOnlyTextProvider.Instance);
                CurrentCatalog = null;
                return;
            }
        }

        CurrentCatalog = catalog;
        LocalizationService.SetTextProvider(new CatalogTextProvider(catalog));
    }

    /// <summary>
    /// 读设置（语言）。设置服务是 <see cref="AppServices.Settings"/>；
    /// 读不到/抛错时返回 null（调用方按「未设置」处理，等于跟随系统）。
    /// </summary>
    private static string? ReadSetting(string key)
    {
        try
        {
            return SettingsReader?.Invoke(key) ?? AppSettings.Settings.Get<string>(key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 设置读取的**可替换实现**（**仅供测试**）。
    ///
    /// <para>
    /// 存在的原因：设置服务是进程级单例，测试里既造不出"读设置会抛异常"的状态，
    /// 也无法让"写设置失败"变得可观测 —— 上一轮注入矩阵里，删掉写失败的日志分支
    /// **一个测试都没红**，说明那条路径根本没被覆盖。这里给一条显式的缝，
    /// 让它能被测到。
    /// </para>
    /// </summary>
    public static Func<string, string?>? SettingsReader { get; set; }

    /// <summary>
    /// 写设置（语言）。写失败只记日志，不抛 —— 语言切换不该让界面崩掉。
    /// </summary>
    private static void WriteSetting(string key, string value)
    {
        try
        {
            if (SettingsWriter is not null) { SettingsWriter(key, value); return; }
            AppSettings.Settings.Set(key, value);
        }
        catch (Exception ex)
        {
            Log.Warn("Localization", $"保存语言设置失败：{ex.Message}");
        }
    }
}
