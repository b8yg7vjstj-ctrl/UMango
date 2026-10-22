using System.Globalization;
using UMango.Core.Localization;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 本地化核心的测试（平台中立部分）。
/// </summary>
public class LocalizationServiceTests
{
    [Fact]
    public void 默认语言是简体中文且是清单第一项()
    {
        Assert.Equal("zh-CN", LocalizationModels.DefaultLanguageCode);
        Assert.Equal("zh-CN", LocalizationModels.SupportedLanguages[0].Code);
    }

    [Fact]
    public void auto解析为默认语言()
    {
        var lang = LocalizationModels.ResolveLanguage("auto");
        Assert.Equal("zh-CN", lang.Code);
    }

    [Fact]
    public void 空值与null按auto处理()
    {
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage(null).Code);
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage("").Code);
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage("   ").Code);
    }

    [Fact]
    public void 下划线形式被归一化()
    {
        Assert.Equal("en-US", LocalizationModels.ResolveLanguage("en_US").Code);
    }

    [Fact]
    public void 不受支持的代码回落默认语言()
    {
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage("xx-XX").Code);
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage("klingon").Code);
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.Equal("en-US", LocalizationModels.ResolveLanguage("EN-us").Code);
        Assert.Equal("zh-CN", LocalizationModels.ResolveLanguage("ZH-cn").Code);
    }

    [Fact]
    public void 空代码视为受支持表示auto()
    {
        Assert.True(LocalizationService.IsLanguageSupported(null));
        Assert.True(LocalizationService.IsLanguageSupported(""));
        Assert.True(LocalizationService.IsLanguageSupported("auto"));
    }

    [Fact]
    public void 已支持的语言判定为受支持()
    {
        Assert.True(LocalizationService.IsLanguageSupported("zh-CN"));
        Assert.True(LocalizationService.IsLanguageSupported("en-US"));
    }

    [Fact]
    public void 未支持的语言判定为不受支持()
    {
        Assert.False(LocalizationService.IsLanguageSupported("ja-JP"));
        Assert.False(LocalizationService.IsLanguageSupported("de-DE"));
    }

    [Fact]
    public void 系统语言解析总是返回受支持的语言()
    {
        var lang = LocalizationModels.ResolveSystemLanguage();
        Assert.Contains(lang, LocalizationModels.SupportedLanguages);
    }

    [Fact]
    public void 应用到英文后当前语言改变()
    {
        var before = LocalizationService.CurrentLanguage.Code;
        var changed = LocalizationService.Apply("en-US");
        Assert.True(changed, "从默认语言切到英文应报告变化");
        Assert.Equal("en-US", LocalizationService.CurrentLanguage.Code);
        Assert.Equal("en-US", LocalizationService.CurrentFormatCulture.Name);

        // 还原，避免污染同集合里的其它用例
        LocalizationService.Apply("zh-CN");
        Assert.Equal("zh-CN", LocalizationService.CurrentLanguage.Code);
        Assert.Equal(before, LocalizationService.CurrentLanguage.Code);
    }

    [Fact]
    public void 重复应用同一语言不报告变化()
    {
        LocalizationService.Apply("zh-CN");
        Assert.False(LocalizationService.Apply("zh-CN"), "无变化应返回 false，调用方据此跳过无谓刷新");
    }

    [Fact]
    public void 展示格式可独立于UI语言()
    {
        LocalizationService.Apply("zh-CN", "en-US");
        Assert.Equal("zh-CN", LocalizationService.CurrentLanguage.Code);
        Assert.Equal("en-US", LocalizationService.CurrentFormatCulture.Name);

        LocalizationService.Apply("zh-CN");   // 还原
        Assert.Equal("zh-CN", LocalizationService.CurrentFormatCulture.Name);
    }

    [Fact]
    public void 展示格式跟随UI语言时取语言自己的区域性()
    {
        LocalizationService.Apply("en-US", LocalizationModels.FormatCultureFollowLanguage);
        Assert.Equal("en-US", LocalizationService.CurrentFormatCulture.Name);
        LocalizationService.Apply("zh-CN");
    }

    [Fact]
    public void 语言变化会触发事件()
    {
        var fired = 0;
        void Handler() => fired++;
        LocalizationService.LanguageChanged += Handler;
        try
        {
            LocalizationService.Apply("en-US");
            Assert.Equal(1, fired);

            LocalizationService.Apply("en-US");   // 无变化不应再触发
            Assert.Equal(1, fired);

            LocalizationService.Apply("zh-CN");
            Assert.Equal(2, fired);
        }
        finally
        {
            LocalizationService.LanguageChanged -= Handler;
            LocalizationService.Apply("zh-CN");
        }
    }

    [Fact]
    public void 非法区域性名不会抛异常而是回落()
    {
        var culture = LocalizationService.ResolveFormatCulture("not-a-culture");
        Assert.NotNull(culture);
        Assert.Equal(CultureInfo.InvariantCulture, culture);
    }

    [Fact]
    public void 展示格式auto跟随系统()
    {
        var culture = LocalizationService.ResolveFormatCulture(LocalizationModels.Auto);
        Assert.Equal(CultureInfo.CurrentCulture, culture);
    }
}
