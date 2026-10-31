using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UMango.Core.Localization;
using UMango.Shell.Localization;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 本地化文案与调用点的**回归测试**。
///
/// <para>
/// 每一条都对应一个**真实发生过的缺陷**，而不是"以防万一"的装饰性断言：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>启动即崩溃</b>：诊断页把 <c>Tools.Report.RecordCount</c>（3 个占位符）
///     当单参调用，<c>string.Format</c> 抛 <c>FormatException</c>，
///     而它发生在<b>构造函数</b>里 —— Avalonia 壳因此启动即崩。
///   </item>
///   <item>
///     <b>语言间漂移</b>：<c>Download.Empty.NoInstalled</c> 中文 2 个占位符、
///     英文 1 个 —— 中文界面正常，<b>切到英文</b>才崩。
///   </item>
///   <item>
///     <b>错键</b>：Avalonia 设置页用了不存在的 <c>Common.Cancel</c> /
///     <c>Common.Ok</c>，界面上直接显示键名本身。
///   </item>
///   <item>
///     <b>中文特有的 hack</b>：英文文案里被切掉首字（<c>noun[1..]</c>），
///     切完变成 "nstalled packages"。
///   </item>
/// </list>
///
/// <para>
/// 静态层面的同类问题（任意调用点的元数不匹配）由
/// <c>tools/LocalizationAudit</c> 在 CI 阶段拦截；
/// 这里守的是"语言文件本身"与"本次修过的具体键"。
/// </para>
/// </summary>
[Collection("静态状态")]
public class LocalizationRegressionTests : IDisposable
{
    private readonly ITextProvider _originalProvider;

    public LocalizationRegressionTests()
    {
        // 本类会替换 Core 的全局文案提供器，结束后必须还原。
        _originalProvider = LocalizationService.CurrentProvider;
    }

    public void Dispose() => LocalizationService.SetTextProvider(_originalProvider);

    // ===== 启动崩溃 =====

    [Fact]
    public void 记录条数模板_两种语言都需要三个占位符()
    {
        // 崩溃点的根因：这个模板有 3 个占位符（总数/成功/失败），
        // 调用点必须给 3 个实参。少给一个 → FormatException → 启动即崩。
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var indices = PlaceholderIndices(Catalog(code).Text("Tools.Report.RecordCount"));
            Assert.Equal(new[] { 0, 1, 2 }, indices);
        }
    }

    [Fact]
    public void 记录条数_按三个计数格式化不抛异常且结果含三个数字()
    {
        // 走**真实**的 Core 格式化路径（崩溃就发生在这一层），
        // 而不是只查模板 —— 模板对但格式化参数不对，同样会崩。
        LocalizationService.SetTextProvider(new CatalogTextProvider(Catalog("zh-CN")));

        var text = LocalizationService.Text("Tools.Report.RecordCount", 7, 5, 2);

        Assert.Contains("7", text);
        Assert.Contains("5", text);
        Assert.Contains("2", text);
    }

    [Fact]
    public void 记录条数_实参不足时不抛异常()
    {
        // 类文档承诺"不会抛异常"：即便将来有人改坏调用点，
        // 也只该得到一句缺字的文案，绝不能把应用带崩。
        LocalizationService.SetTextProvider(new CatalogTextProvider(Catalog("zh-CN")));

        var ex = Record.Exception(() => LocalizationService.Text("Tools.Report.RecordCount", 7));

        Assert.Null(ex);
    }

    // ===== 语言间模板漂移 =====

    [Fact]
    public void 两种语言_共有键的占位符集合一致()
    {
        // 全量检查（不只查这一次修的那一个键）：
        // 同一个键在不同语言里换模板时，占位符个数必须一样，
        // 否则切语言的那一瞬间就会抛 FormatException 或静默丢字。
        var zh = Catalog("zh-CN");
        var en = Catalog("en-US");

        var drifted = new List<string>();
        foreach (var (key, enTemplate) in en.AllEntries())
        {
            if (!zh.Contains(key)) continue;   // 只有英文才有的键没有对照，跳过
            if (zh.Text(key) == enTemplate) continue;

            var expected = PlaceholderIndices(zh.Text(key));
            var actual = PlaceholderIndices(enTemplate);
            if (!expected.SequenceEqual(actual))
            {
                drifted.Add($"{key}：zh-CN={Format(expected)}，en-US={Format(actual)}");
            }
        }

        Assert.True(drifted.Count == 0,
            $"有 {drifted.Count} 个键的占位符在两种语言间不一致：{string.Join("；", drifted.Take(10))}");
    }

    [Fact]
    public void 下载页空态模板_两种语言都需要两个占位符()
    {
        // 这条曾真的漂移过：中文 "{0} 下没有{1}"，英文只有 "{0}"。
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var indices = PlaceholderIndices(Catalog(code).Text("Download.Empty.NoInstalled"));
            Assert.Equal(new[] { 0, 1 }, indices);
        }
    }

    [Fact]
    public void 下载页数量模板_两种语言都需要三个占位符()
    {
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var indices = PlaceholderIndices(Catalog(code).Text("Download.Count.Installed"));
            Assert.Equal(new[] { 0, 1, 2 }, indices);
        }
    }

    // ===== 错键 =====

    [Fact]
    public void 通用按钮文案_两种语言里都存在()
    {
        // Avalonia 设置页的确认/取消按钮用的就是这两个键。
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var catalog = Catalog(code);
            Assert.True(catalog.Contains("Common.Action.Confirm"), $"{code} 缺少 Common.Action.Confirm");
            Assert.True(catalog.Contains("Common.Action.Cancel"), $"{code} 缺少 Common.Action.Cancel");
        }
    }

    [Fact]
    public void 不存在的旧键_任何语言里都没有()
    {
        // 曾经用错的两个键。断言它们**不存在**，
        // 是为了防止"干脆把错键补进语言文件"来糊弄审计 ——
        // 那样界面上会显示键名本身（Common.Cancel），是用户可见的缺陷。
        foreach (var code in new[] { "zh-CN", "en-US" })
        {
            var catalog = Catalog(code);
            Assert.False(catalog.Contains("Common.Cancel"), $"{code} 不该有 Common.Cancel");
            Assert.False(catalog.Contains("Common.Ok"), $"{code} 不该有 Common.Ok");
        }
    }

    // ===== 中文特有的名词 hack =====

    [Fact]
    public void 已安装包名词_英文版没有被切掉首字()
    {
        // 老写法在调用点切首字（切中文的量词"个"）：
        //   中文 noun = "个已安装包" → noun[1..] = "已安装包"
        //   英文 noun = "installed packages" → noun[1..] = "nstalled packages" ✗
        // 现在量词挪进了模板，名词值两种语言共用同一份。
        Assert.Equal("installed packages", Catalog("en-US").Text("Download.Noun.Installed"));
        Assert.Equal("已安装包", Catalog("zh-CN").Text("Download.Noun.Installed"));
    }

    [Fact]
    public void 已安装包数量句_中文的量词在模板里而不是名词里()
    {
        // 量词属于句子的语法位置，不属于名词本身。
        Assert.Contains("个", Catalog("zh-CN").Text("Download.Count.Installed"));
        Assert.DoesNotContain("个", Catalog("zh-CN").Text("Download.Noun.Installed"));
    }

    // ===== 辅助 =====

    private static LanguageCatalog Catalog(string code) => LanguageCatalog.Load(code);

    /// <summary>取出模板里用到的占位符索引（去重、升序）。</summary>
    private static int[] PlaceholderIndices(string template)
    {
        var set = new SortedSet<int>();
        foreach (Match m in Regex.Matches(template, @"(?<!\{)\{(\d+)"))
        {
            if (int.TryParse(m.Groups[1].Value, out var index)) set.Add(index);
        }
        return set.ToArray();
    }

    private static string Format(IReadOnlyList<int> indices)
        => indices.Count == 0 ? "无" : string.Join(",", indices.Select(i => $"{{{i}}}"));
}
