using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UMango.Tools.XamlAudit;

/// <summary>一个 XAML 文件里提取出来的、需要在运行时才能解析的引用。</summary>
public sealed class XamlFile
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    /// <summary>本文件里定义（x:Key）的资源键</summary>
    public HashSet<string> DefinedKeys { get; } = new(StringComparer.Ordinal);
    /// <summary>引用的资源键 → 出现位置（行号，1 起）</summary>
    public List<(string Key, int Line, bool IsDynamic)> ResourceRefs { get; } = [];
    /// <summary>绑定路径 → 行号</summary>
    public List<(string Path, int Line, string? ElementType)> Bindings { get; } = [];
    /// <summary>XAML 里挂的事件处理器名 → 行号</summary>
    public List<(string Handler, int Line)> EventHandlers { get; } = [];
    /// <summary>XAML 里声明的 x:Name</summary>
    public HashSet<string> NameDeclarations { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// 模板键 → 该模板绑定的元素类型。
    /// 形如 {StaticResource NavItemTemplate} 的引用拿不到结构上的嵌套关系，
    /// 只能靠"引用处容器的 ItemsSource 集合元素类型"反推，结果记在这里。
    /// </summary>
    public Dictionary<string, string> TemplateElementTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>位于带 x:Key 的模板内部的绑定：路径、行号、所属模板键</summary>
    public List<(string Path, int Line, string TemplateKey)> KeyedTemplateBindings { get; } = [];

    public override string ToString() => RelativePath;
}

/// <summary>
/// XAML 解析器。
///
/// **为什么自己解析而不是用 WPF 的 XamlReader**：WPF 在 macOS 上加载不了
/// （需要 Microsoft.WindowsDesktop.App 运行时）。所以这里用 XDocument 做纯 XML 解析，
/// 把"需要在运行时解析的东西"全部抽出来，再逐类校验。
/// </summary>
public static class XamlIndex
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static List<XamlFile> LoadAll(string appRoot, CSharpIndex code)
    {
        var result = new List<XamlFile>();
        foreach (var path in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var file = new XamlFile
            {
                Path = path,
                RelativePath = Path.GetRelativePath(appRoot, path)
            };

            XDocument doc;
            try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
            catch (Exception) { continue; }   // XAML 语法错误交给编译器报，这里不做重复检查

            var dataContextType = FindDataContextType(path);
            Walk(doc.Root, file, dataContextType, code, templateKey: null);
            result.Add(file);
        }
        return result;
    }

    private static void Walk(XElement? element, XamlFile file, string? scopeType,
                             CSharpIndex code, string? templateKey)
    {
        if (element is null) return;

        // 1) 资源定义：任何带 x:Key 的元素。
        //    注意模板元素自身也要走到 —— 之前把 DataTemplate 只当"容器"处理、
        //    跳过它自己的 x:Key，导致 `ItemTemplate="{StaticResource Xxx}"` 全被误报为键不存在。
        var key = element.Attribute(X + "Key")?.Value;
        if (!string.IsNullOrEmpty(key)) file.DefinedKeys.Add(key);

        // 2) x:Name
        var nameAttr = element.Attribute(X + "Name")?.Value ?? element.Attribute("Name")?.Value;
        if (!string.IsNullOrEmpty(nameAttr)) file.NameDeclarations.Add(nameAttr);

        // 3) 该元素若是"绑定集合 + 模板"的容器，先算出模板的作用域类型
        var childScope = ResolveItemsScope(element, scopeType, code);

        // 4) 属性
        foreach (var attr in element.Attributes())
        {
            var value = attr.Value;
            var line = LineOf(attr);

            foreach (Match m in Regex.Matches(value, @"\{(DynamicResource|StaticResource)\s+([^}]+)\}"))
            {
                var isDynamic = m.Groups[1].Value == "DynamicResource";
                var resKey = m.Groups[2].Value.Trim();
                if (resKey.Length > 0) file.ResourceRefs.Add((resKey, line, isDynamic));
            }

            // 模板引用：把"模板键 → 元素类型"记下来，供按 key 引用的模板内部绑定校验
            if (attr.Name.LocalName is "ItemTemplate" or "CellTemplate" or "ContentTemplate")
            {
                var refKey = ExtractResourceKey(value);
                if (refKey is not null && childScope is not null)
                {
                    file.TemplateElementTypes[refKey] = childScope;
                }
            }

            if (value.Contains("{Binding", StringComparison.Ordinal))
            {
                var path = ExtractBindingPath(value);
                if (path is not null)
                {
                    if (templateKey is not null) file.KeyedTemplateBindings.Add((path, line, templateKey));
                    else file.Bindings.Add((path, line, scopeType));
                }
            }

            if (attr.Name.NamespaceName.Length == 0 && value.Length > 0
                && !value.Contains('{') && !value.Contains(' ')
                && (attr.Name.LocalName.Contains("Click", StringComparison.OrdinalIgnoreCase)
                    || attr.Name.LocalName.EndsWith("Changed", StringComparison.Ordinal)
                    || attr.Name.LocalName.EndsWith("Loaded", StringComparison.Ordinal)))
            {
                if (Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    file.EventHandlers.Add((value, line));
                }
            }
        }

        if (!string.IsNullOrEmpty(key))
        {
            // 带 x:Key 的模板：其内部绑定的作用域是"引用处集合的元素类型"，
            // 但定义点通常拿不到引用处 → 这里先把 key 传给内部，等 Audit 阶段反查。
            foreach (var child in element.Elements())
            {
                Walk(child, file, childScope, code, key);
            }
            return;
        }

        foreach (var child in element.Elements())
        {
            // 统一走 Walk —— **不要**为 DataTemplate 单独开分支。
            //
            // 这里踩过两次同一个坑：原先写成了"把 DataTemplate 当纯容器、只遍历它的子元素"，
            // 结果 DataTemplate 元素**自己**没被 Walk 到，它的 x:Key 就收不进 DefinedKeys，
            // 于是所有 `ItemTemplate="{StaticResource Xxx}"` 全被误报为"键不存在"。
            // （`<Style>` 没被特殊对待，所以只有 Style 的键能收到 —— 这个不对称正是线索。）
            // 正确做法：DataTemplate 自己也要 Walk（收 x:Key），作用域由 childScope 传下去。
            Walk(child, file, childScope ?? scopeType, code, templateKey);
        }
    }

    /// <summary>
    /// 若该元素是「ItemsSource 绑定集合 + 有模板」的容器，返回集合的元素类型名；否则返回 null。
    /// 拿不到类型就返回 null —— **宁可不查，也不要拿错类型去校验、制造一堆假报错**。
    /// </summary>
    private static string? ResolveItemsScope(XElement element, string? scopeType, CSharpIndex code)
    {
        if (scopeType is null) return null;

        var itemsSource = element.Attribute("ItemsSource")?.Value;
        if (string.IsNullOrEmpty(itemsSource)) return null;

        var prop = ExtractBindingPath(itemsSource);
        if (prop is null) return null;

        var type = code.Find(scopeType);
        if (type is null || !type.PropertyTypes.TryGetValue(prop, out var propType)) return null;

        return CSharpIndex.ElementTypeOf(propType);
    }

    /// <summary>从 {StaticResource X} / {DynamicResource X} 里取出资源键。</summary>
    public static string? ExtractResourceKey(string markup)
    {
        var m = Regex.Match(markup, @"\{(?:Static|Dynamic)Resource\s+([^}]+)\}"); 
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>从 code-behind 里找出 DataContext 被设成什么类型。</summary>
    private static string? FindDataContextType(string xamlPath)
    {
        var codeBehind = xamlPath + ".cs";
        if (!File.Exists(codeBehind)) return null;

        var text = File.ReadAllText(codeBehind);
        var m = Regex.Match(text, @"DataContext\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ExtractBindingPath(string markup)
    {
        var m = Regex.Match(markup, @"\{Binding\s+([^},]*)");
        if (!m.Success) return null;

        var inner = m.Groups[1].Value.Trim();
        if (inner.Length == 0) return null;                 // {Binding} 绑定自身，无路径
        if (inner.StartsWith("Path=", StringComparison.Ordinal))
        {
            inner = inner[5..].Trim();
        }
        if (inner.Length == 0 || inner.Contains('{')) return null;

        // 去掉索引器与相对源等修饰，只留第一段属性名
        var first = inner.Split('.')[0].Split('[')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    private static int LineOf(XAttribute attr)
        => (attr as IXmlLineInfo)?.HasLineInfo() == true ? ((IXmlLineInfo)attr).LineNumber : 0;
}
