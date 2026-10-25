using System;
using UMango.Core.Localization;
using UMango.Shell.Localization;

namespace UMango.Shell.Localization;

/// <summary>
/// 把 <see cref="LanguageCatalog"/> 适配成 Core 的 <see cref="ITextProvider"/>。
///
/// 这样业务层 / ViewModel 可以在**不引用任何 UI 框架类型**的前提下取本地化文案
/// （WPF 的 <c>Application</c> 会拖入 PresentationFramework，它只存在于 Windows；
/// macOS 的测试宿主碰到就 <c>FileNotFoundException</c>）。
/// </summary>
public sealed class CatalogTextProvider : ITextProvider
{
    private readonly LanguageCatalog _catalog;

    public CatalogTextProvider(LanguageCatalog catalog) => _catalog = catalog;

    /// <summary>底层目录（需要按语言取值时用）。</summary>
    public LanguageCatalog Catalog => _catalog;

    /// <summary>取文案；找不到键时返回键本身。</summary>
    public string Text(string key) => _catalog.Text(key);
}
