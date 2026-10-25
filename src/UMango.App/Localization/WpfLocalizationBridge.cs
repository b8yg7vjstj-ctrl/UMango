using System;
using System.Collections.Generic;
using System.Windows;
using UMango.Core.Logging;
using UMango.Shell.Localization;

namespace UMango.App.Localization;

/// <summary>
/// WPF 侧的本地化**薄适配层**：把 <see cref="ShellLocalization"/>（平台中立）接到
/// WPF 的 <see cref="ResourceDictionary"/> 机制上。
///
/// <para>
/// 分工：
/// <list type="bullet">
///   <item><c>ShellLocalization</c>（Shell，net10.0）：读设置、应用语言、发布文案提供器 —— 平台中立；</item>
///   <item><b>本类</b>（App，net10.0-windows）：只做「把文案灌进 WPF 资源字典」这一件 WPF 专属的事。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么要有这一层</b>：<c>Application.Current</c> / <c>ResourceDictionary</c> 都是
/// WPF 类型，只存在于 Windows。Avalonia 壳读的是同一份内嵌纯文本语言文件，
/// 自己灌进自己的资源字典 —— 两边共用同一份真相源，不可能分叉。
/// </para>
///
/// <para>
/// <b>思路来源</b>：PCL-CE <c>LocalizationService._ApplyLanguageResourcesCore</c>（Apache-2.0）。
/// </para>
/// </summary>
public static class WpfLocalizationBridge
{
    /// <summary>设置项键：UI 语言（转发到平台中立的常量）。</summary>
    public const string LanguageSettingKey = ShellLocalization.LanguageSettingKey;

    /// <summary>设置项键：展示格式区域性（转发到平台中立的常量）。</summary>
    public const string FormatCultureSettingKey = ShellLocalization.FormatCultureSettingKey;

    /// <summary>当前生效的语言目录（供需要按语言取值的场合使用）。</summary>
    public static UMango.Shell.Localization.LanguageCatalog? CurrentCatalog => ShellLocalization.CurrentCatalog;

    /// <summary>
    /// 取当前语言下的文本（供 code-behind 用）。找不到键时返回键本身。
    ///
    /// <para>
    /// 走 <see cref="ShellLocalization.Text"/> → Core 的文案提供器 → 语言目录，
    /// 与界面上显示的文案**同源**，不经过 <c>Application.TryFindResource</c>。
    /// </para>
    /// </summary>
    public static string Text(string key) => ShellLocalization.Text(key);

    /// <summary>
    /// 读已保存的语言代码（供设置页回显选中项）。
    /// 未保存过时返回 <c>auto</c>。
    /// </summary>
    public static string ReadSavedLanguageCode() => ShellLocalization.ReadSavedLanguageCode();

    /// <summary>供设置页下拉框用的语言选项（首项 = 跟随系统）。</summary>
    public static IReadOnlyList<ShellLocalization.LanguageOption> BuildOptions() => ShellLocalization.BuildOptions();

    /// <summary>
    /// 上次灌入的语言字典实例。
    ///
    /// <para>
    /// **只移除自己那一个** —— 应用级 <see cref="ResourceDictionary.MergedDictionaries"/>
    /// 里还有主题字典（PclTokens / Styles / Controls.Pcl），全清会把主题一起干掉，
    /// 界面会瞬间退回 WPF 默认外观。
    /// </para>
    /// </summary>
    private static ResourceDictionary? _languageDictionary;

    /// <summary>
    /// 挂上宿主钩子：平台中立层换好文案后，WPF 这边把文案灌进资源字典。
    ///
    /// <para>
    /// 用 <see cref="Hook"/> 而不是"每次调用都自己灌一遍"，是为了让
    /// <c>ShellLocalization.SetLanguage</c> 成为**唯一入口** —— 两个壳都走同一条路径，
    /// 不可能出现"一个壳换了语言、另一个没换"。
    /// </para>
    /// </summary>
    public static void Hook()
    {
        // 钩子是"可覆盖"的单个委托（不是事件），所以先摘掉旧的再挂新的，
        // 保证重复调用 Hook 不会把重建逻辑跑两遍。
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

    /// <summary>
    /// 切换语言（供设置页调用）。会持久化并刷新界面。
    /// </summary>
    /// <param name="code">语言代码，<c>auto</c> = 跟随系统。</param>
    public static void SetLanguage(string code) => ShellLocalization.SetLanguage(code);

    /// <summary>
    /// 语言切换后触发（在资源字典已重建之后）。
    ///
    /// <para>
    /// 订阅方用于刷新**代码侧拼的本地化句子**：XAML 里的 {DynamicResource} 会随
    /// 字典替换自动更新，但 ViewModel 里 string.Format 出来的字符串不会，
    /// 必须显式重建（见 HomeViewModel.RefreshLocalizedText）。
    /// </para>
    /// </summary>
    public static event Action? LocalizedTextRefreshRequested
    {
        add => ShellLocalization.LocalizedTextRefreshRequested += value;
        remove => ShellLocalization.LocalizedTextRefreshRequested -= value;
    }

    /// <summary>
    /// 重建资源字典栈：清掉旧条目，灌入**当前语言合并后**的文案。
    /// 必须在 UI 线程调用（<see cref="Application.Resources"/> 是 DispatcherObject）。
    ///
    /// <para>
    /// 为什么不按「兜底层 + 叠加层」两层分别灌：合并后灌一次即可 ——
    /// 两层合并的语义与 <c>LanguageCatalog</c> 完全一致（叠加层覆盖兜底层），
    /// 而 WPF 的 <see cref="ResourceDictionary"/> 本来就是后者优先。
    /// 少一层 = 少一个可能分叉的地方。
    /// </para>
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

        // 只动自己那一个，别碰主题字典。
        var dicts = app.Resources.MergedDictionaries;
        if (_languageDictionary is not null) dicts.Remove(_languageDictionary);

        var dictionary = new ResourceDictionary();
        foreach (var pair in catalog.AllEntries()) dictionary.Add(pair.Key, pair.Value);
        dicts.Add(dictionary);
        _languageDictionary = dictionary;
    }
}
