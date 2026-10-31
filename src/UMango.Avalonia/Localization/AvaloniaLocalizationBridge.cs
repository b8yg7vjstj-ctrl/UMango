// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的本地化**薄适配层**：把 `UMango.Shell.Localization.ShellLocalization`
//（平台中立）接到 Avalonia 的资源机制上。
//
// 与 WPF 侧的 `src/UMango.App/Localization/WpfLocalizationBridge.cs` **同构**：
// 同一份内嵌纯文本语言文件、同一条切换路径，只是"灌进哪个资源容器"不同。
//
// WPF 灌进 `ResourceDictionary`（键值对），Avalonia 灌进 `Application.Resources`
// 的 `ResourceDictionary`，用 `x:String` 承载纯文本 —— Avalonia 11 支持
// `x:String` 作为资源项，界面上就能用 `{DynamicResource Log.Refresh}` 取到。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UMango.Core.Logging;
using UMango.Shell.Localization;

namespace UMango.Avalonia.Localization;

/// <summary>
/// Avalonia 侧的本地化桥。
/// </summary>
public static class AvaloniaLocalizationBridge
{
    /// <summary>设置项键：UI 语言（转发到平台中立的常量）。</summary>
    public const string LanguageSettingKey = ShellLocalization.LanguageSettingKey;

    /// <summary>设置项键：展示格式区域性（转发到平台中立的常量）。</summary>
    public const string FormatCultureSettingKey = ShellLocalization.FormatCultureSettingKey;

    /// <summary>当前生效的语言目录（供需要按语言取值的场合使用）。</summary>
    public static LanguageCatalog? CurrentCatalog => ShellLocalization.CurrentCatalog;

    /// <summary>取当前语言下的文本（供 code-behind 用）。找不到键时返回键本身。</summary>
    public static string Text(string key) => ShellLocalization.Text(key);

    /// <summary>读已保存的语言代码（供设置页回显选中项）。未保存过时返回 <c>auto</c>。</summary>
    public static string ReadSavedLanguageCode() => ShellLocalization.ReadSavedLanguageCode();

    /// <summary>供设置页下拉框用的语言选项（首项 = 跟随系统）。</summary>
    public static IReadOnlyList<ShellLocalization.LanguageOption> BuildOptions() => ShellLocalization.BuildOptions();

    /// <summary>
    /// 上次灌入的语言资源字典。
    ///
    /// <para>
    /// **只移除自己那一个** —— 应用级资源里还有主题字典（Br* 画刷、UiFont 等），
    /// 全清会把主题一起干掉，界面会瞬间退回 Avalonia 默认外观。
    /// </para>
    /// </summary>
    private static IResourceDictionary? _languageDictionary;

    /// <summary>
    /// 挂上宿主钩子：平台中立层换好文案后，Avalonia 这边把文案灌进资源字典。
    ///
    /// <para>
    /// 与 WPF 侧一样用"先摘旧的再挂新的"，保证重复调用不会把重建逻辑跑两遍。
    /// </para>
    /// </summary>
    public static void Hook()
    {
        var previous = ShellLocalization.HostDictionariesRebuilt;
        ShellLocalization.HostDictionariesRebuilt = () =>
        {
            previous?.Invoke();
            ApplyDictionaries();
        };
    }

    /// <summary>
    /// 启动时调用：按已保存的设置应用语言，并把文案灌进资源字典。
    /// 必须在主窗口创建**之前**调用，否则首帧会用默认语言画一遍再跳。
    /// </summary>
    public static void ApplySavedLanguage()
    {
        Hook();
        ShellLocalization.ApplySavedLanguage();
    }

    /// <summary>切换语言（供设置页调用）。会持久化并刷新界面。</summary>
    public static void SetLanguage(string code) => ShellLocalization.SetLanguage(code);

    /// <summary>
    /// 语言切换后触发（在资源字典已重建之后）。
    ///
    /// <para>
    /// 订阅方用于刷新**代码侧拼的本地化句子**：XAML 里的 {DynamicResource} 会随
    /// 资源替换自动更新，但 ViewModel 里 string.Format 出来的字符串不会。
    /// </para>
    /// </summary>
    public static event Action? LocalizedTextRefreshRequested
    {
        add => ShellLocalization.LocalizedTextRefreshRequested += value;
        remove => ShellLocalization.LocalizedTextRefreshRequested -= value;
    }

    /// <summary>
    /// 重建资源字典：清掉旧条目，灌入**当前语言合并后**的文案。
    /// 必须在 UI 线程调用。
    /// </summary>
    private static void ApplyDictionaries()
    {
        var app = Application.Current;
        if (app is null) return;

        var catalog = ShellLocalization.CurrentCatalog;
        if (catalog is null)
        {
            Log.Warn("Localization", "当前没有可用的语言目录，界面文案将退化为键名。");
            return;
        }

        var dicts = app.Resources.MergedDictionaries;
        if (_languageDictionary is not null) dicts.Remove(_languageDictionary);

        var dictionary = new ResourceDictionary();
        foreach (var pair in catalog.AllEntries())
        {
            // 用字符串键（而不是 `new ResourceKey(...)`）：XAML 里的
            // `{DynamicResource Log.Refresh}` 解析出来就是字符串键，
            // 两边必须同一种键类型才查得到。
            dictionary.Add(pair.Key, pair.Value);
        }

        dicts.Add(dictionary);
        _languageDictionary = dictionary;
    }
}
