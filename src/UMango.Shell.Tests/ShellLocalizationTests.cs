using System;
using System.IO;
using System.Linq;
using UMango.Core.Configuration;
using UMango.Core.Localization;
using UMango.Shell.Localization;
using UMango.Shell.Services;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 平台中立的本地化编排层（<see cref="ShellLocalization"/>）的测试。
///
/// 覆盖三件事：
/// ① 语言设置项**已注册**（没注册的话 <c>Settings.Set</c> 会抛 <c>KeyNotFoundException</c>，
///    语言选择就永远存不下来 —— 这是一个真实发生过的缺陷）；
/// ② 语言切换后文案提供器真的换了，且界面用的键能取到值；
/// ③ 宿主钩子的调用顺序：字典先重建，再通知代码侧句子重建。
/// </summary>
[Collection("静态状态")]
public class ShellLocalizationTests : IDisposable
{
    public ShellLocalizationTests()
    {
        // 本类会改 Core 的全局语言状态与文案提供器，测试结束后要还原。
        _originalProvider = LocalizationService.CurrentProvider;
        _originalLanguage = LocalizationService.CurrentLanguage.Code;
    }

    private readonly ITextProvider _originalProvider;
    private readonly string _originalLanguage;

    public void Dispose()
    {
        LocalizationService.SetTextProvider(_originalProvider);
        LocalizationService.Apply(_originalLanguage, LocalizationModels.FormatCultureFollowLanguage);
        ShellLocalization.ResetHooks();
    }

    [Fact]
    public void 语言设置项_已注册()
    {
        // 回归：ui.language / ui.formatCulture 曾经从未在 UiSettings 注册，
        // 于是 Settings.Set 抛 KeyNotFoundException，语言选择静默丢失。
        var service = NewService();
        service.Set(UiSettings.LanguageKey, "en-US");
        service.Set(UiSettings.FormatCultureKey, "en-US");

        Assert.Equal("en-US", service.Get<string>(UiSettings.LanguageKey));
        Assert.Equal("en-US", service.Get<string>(UiSettings.FormatCultureKey));
    }

    [Fact]
    public void 语言设置项_默认值是跟随系统与跟随语言()
    {
        var service = NewService();
        Assert.Equal("auto", service.Get<string>(UiSettings.LanguageKey));
        Assert.Equal("ui-language", service.Get<string>(UiSettings.FormatCultureKey));
    }

    [Fact]
    public void 取文案_返回语言目录里的值而不是键本身()
    {
        var catalog = LanguageCatalog.Load("zh-CN");
        LocalizationService.SetTextProvider(new CatalogTextProvider(catalog));

        var key = catalog.AllEntries().First().Key;
        Assert.Equal(catalog.Text(key), ShellLocalization.Text(key));
    }

    [Fact]
    public void 取文案_键不存在时返回键本身()
    {
        LocalizationService.SetTextProvider(new CatalogTextProvider(LanguageCatalog.Load("zh-CN")));
        Assert.Equal("完全不存在的键", ShellLocalization.Text("完全不存在的键"));
    }

    [Fact]
    public void 语言选项_首项是跟随系统且其后是各语言自称()
    {
        var options = ShellLocalization.BuildOptions();
        Assert.Equal(LocalizationModels.Auto, options[0].Code);
        Assert.Equal(1 + LocalizationModels.SupportedLanguages.Count, options.Count);
        Assert.Equal(LocalizationModels.SupportedLanguages[0].Code, options[1].Code);
    }

    [Fact]
    public void 宿主钩子_字典重建先于代码侧句子刷新()
    {
        var order = new System.Collections.Generic.List<string>();
        ShellLocalization.HostDictionariesRebuilt = () => order.Add("dictionaries");
        ShellLocalization.LocalizedTextRefreshRequested += () => order.Add("text");

        ShellLocalization.SetLanguage("en-US");

        Assert.Equal(["dictionaries", "text"], order);
    }

    [Fact]
    public void 切换语言后_文案提供器换成新语言的目录()
    {
        ShellLocalization.SetLanguage("en-US");
        Assert.Equal("en-US", LocalizationService.CurrentLanguage.Code);

        // 新语言目录的键应能取到值（取不到就说明提供器没换）
        var key = ShellLocalization.CurrentCatalog!.AllEntries().First().Key;
        Assert.NotEqual(key, ShellLocalization.Text(key));
    }

    [Fact]
    public void 切换语言后_读取已保存的语言代码能回显()
    {
        try
        {
            ShellLocalization.SetLanguage("en-US");
            Assert.Equal("en-US", ShellLocalization.ReadSavedLanguageCode());
        }
        finally
        {
            // 复原全局设置，别把"en-US"留给后面的用例
            try { ShellLocalization.SetLanguage(LocalizationModels.Auto); } catch { /* 复原失败不影响断言 */ }
        }
    }

    [Fact]
    public void 读已保存的语言代码_未设置过时是跟随系统()
    {
        // 注意：这里测的是**归一化规则**而不是全局设置服务的状态。
        // 直接调 ReadSavedLanguageCode 会读到别的用例写进全局设置的值 → 顺序相关的偶发失败。
        Assert.Equal(LocalizationModels.Auto, LocalizationSetting.NormalizeSetting(null));
        Assert.Equal(LocalizationModels.Auto, LocalizationSetting.NormalizeSetting(""));
    }

    [Fact]
    public void 读已保存的语言代码_非法值退回跟随系统()
    {
        var service = NewService();
        service.Set(UiSettings.LanguageKey, "不存在的语言");
        Assert.Equal(LocalizationModels.Auto, ShellLocalization.ReadSavedLanguageCode());
    }

    [Fact]
    public void 写设置失败时_语言切换不抛异常()
    {
        // 回归：写设置失败（磁盘满 / 只读）不该让语言切换把界面搞崩。
        ShellLocalization.SettingsWriter = (_, _) => throw new IOException("磁盘只读");
        var ex = Record.Exception(() => ShellLocalization.SetLanguage("en-US"));
        Assert.Null(ex);
    }

    [Fact]
    public void 读设置失败时_按未设置处理即跟随系统()
    {
        ShellLocalization.SettingsReader = _ => throw new IOException("设置文件损坏");
        Assert.Equal(LocalizationModels.Auto, ShellLocalization.ReadSavedLanguageCode());
    }

    [Fact]
    public void 读设置优先走可替换实现()
    {
        ShellLocalization.SettingsReader = key => key == UiSettings.LanguageKey ? "en-US" : null;
        Assert.Equal("en-US", ShellLocalization.ReadSavedLanguageCode());
    }

    [Fact]
    public void 写设置优先走可替换实现()
    {
        var written = new System.Collections.Generic.List<string>();
        ShellLocalization.SettingsWriter = (key, value) => written.Add($"{key}={value}");
        ShellLocalization.SetLanguage("en-US");
        Assert.Contains($"{UiSettings.LanguageKey}=en-US", written);
    }

    private static SettingsService NewService()
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);
        return service;
    }
}
