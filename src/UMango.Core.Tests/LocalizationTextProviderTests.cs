using System;
using System.Collections.Generic;
using UMango.Core.Localization;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 文案提供器的契约测试。重点保证「无 UI 环境下取文案不炸」——
/// 这是 App.Tests 里 56 处构造 ViewModel 的前提。
/// </summary>
[Collection("静态状态")]
public class LocalizationTextProviderTests : IDisposable
{
    private readonly ITextProvider _saved;

    public LocalizationTextProviderTests()
    {
        // ⚠ 必须存**provider 实例**，不能存 LocalizationService.Text 这个方法组。
        //   存方法组的话，还原后每次取文案都是
        //     LocalizationService.Text → DelegateProvider.Text → LocalizationService.Text → …
        //   无限递归，直接栈溢出把整个测试进程炸掉。
        //   （这个坑在 App.Tests 的 ChineseProvider 里犯过一次，此处是同一形状的复发。）
        _saved = LocalizationService.CurrentProvider;
    }

    public void Dispose()
    {
        // 还原，避免影响其它用例（静态状态）
        LocalizationService.SetTextProvider(_saved);
    }

    [Fact]
    public void 默认提供器找不到键时返回键本身()
    {
        LocalizationService.SetTextProvider(KeyOnlyTextProvider.Instance);
        Assert.Equal("No.Such.Key", LocalizationService.Text("No.Such.Key"));
    }

    [Fact]
    public void 替换提供器后所有Text调用都走它()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Greeting"] = "你好",
            ["Farewell"] = "再见",
        };
        LocalizationService.SetTextProvider(new DelegateProvider(k => table.TryGetValue(k, out var v) ? v : null));

        Assert.Equal("你好", LocalizationService.Text("Greeting"));
        Assert.Equal("再见", LocalizationService.Text("Farewell"));
        Assert.Equal("Missing", LocalizationService.Text("Missing"));
    }

    [Fact]
    public void 传null则回落默认提供器()
    {
        LocalizationService.SetTextProvider(null!);
        // 不应抛 NRE，而是回到「返回键本身」
        Assert.Equal("Any.Key", LocalizationService.Text("Any.Key"));
    }

    private sealed class DelegateProvider : ITextProvider
    {
        private readonly Func<string, string?> _f;
        public DelegateProvider(Func<string, string?> f) => _f = f;
        public string Text(string key) => _f(key) ?? key;
    }
}
