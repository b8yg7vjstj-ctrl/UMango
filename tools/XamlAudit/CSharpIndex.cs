using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UMango.Tools.XamlAudit;

/// <summary>
/// 用 Roslyn **解析源码文本**建立"类型 → 成员"索引。
///
/// 关键点：这里只做语法分析，**不编译、不加载程序集**。
/// 因为 App 工程是 net10.0-windows + WPF，它的程序集在 macOS 上加载不了
/// （需要 Microsoft.WindowsDesktop.App 运行时），而语法树是纯文本，哪儿都能解析。
/// 这也是这个工具能在 macOS 上跑起来的原因。
/// </summary>
public sealed class CSharpIndex
{
    private readonly Dictionary<string, TypeInfo> _types = new(StringComparer.Ordinal);

    /// <summary>一个类型的可绑定成员</summary>
    public sealed class TypeInfo
    {
        public required string Name { get; init; }
        public required string FullName { get; init; }
        public SortedSet<string> Properties { get; } = new(StringComparer.Ordinal);
        /// <summary>属性名 → 声明的类型文本（用于推断集合元素类型）</summary>
        public Dictionary<string, string> PropertyTypes { get; } = new(StringComparer.Ordinal);
        /// <summary>标记为 [ObservableProperty] 的字段（源生成器会生成对应属性）</summary>
        public SortedSet<string> GeneratedFromFields { get; } = new(StringComparer.Ordinal);
        /// <summary>作为 DataContext 候选的类（名字以 ViewModel/Model 结尾，或继承了 ObservableObject）</summary>
        public bool IsBindableRoot { get; set; }
    }

    public IReadOnlyDictionary<string, TypeInfo> Types => _types;

    /// <summary>解析目录下全部 .cs（跳过 obj/bin）。</summary>
    public static CSharpIndex Build(IEnumerable<string> sourceRoots)
    {
        var index = new CSharpIndex();
        foreach (var root in sourceRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                index.AddFile(file);
            }
        }
        return index;
    }

    private void AddFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception) { return; }

        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var name = typeDecl.Identifier.ValueText;
            if (string.IsNullOrEmpty(name)) continue;

            if (!_types.TryGetValue(name, out var info))
            {
                info = new TypeInfo
                {
                    Name = name,
                    FullName = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                        .Select(n => n.Name.ToString()).FirstOrDefault() is { Length: > 0 } ns
                        ? $"{ns}.{name}" : name
                };
                _types[name] = info;
            }

            // 基类里出现 ObservableObject / INotifyPropertyChanged 的，视为可绑定根
            if (typeDecl.BaseList is not null)
            {
                foreach (var b in typeDecl.BaseList.Types)
                {
                    var baseName = b.Type.ToString();
                    if (baseName.Contains("ObservableObject") || baseName.Contains("INotifyPropertyChanged"))
                    {
                        info.IsBindableRoot = true;
                    }
                }
            }

            // record 的**位置参数会生成同名属性** —— 必须收进来。
            // 漏掉它们时，像 `record DownloadEngineInfo(string Id, bool IsAvailable, ...)`
            // 这样的类型会被当成"一个属性都没有"，于是模板里的绑定全被误报为不存在。
            if (typeDecl is RecordDeclarationSyntax record && record.ParameterList is not null)
            {
                foreach (var p in record.ParameterList.Parameters)
                {
                    var propName = p.Identifier.ValueText;
                    if (propName.Length == 0) continue;
                    info.Properties.Add(propName);
                    if (p.Type is not null) info.PropertyTypes[propName] = p.Type.ToString();
                }
            }

            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case PropertyDeclarationSyntax prop:
                        info.Properties.Add(prop.Identifier.ValueText);
                        info.PropertyTypes[prop.Identifier.ValueText] = prop.Type.ToString();
                        break;

                    case FieldDeclarationSyntax field:
                    {
                        // partial 字段 + [ObservableProperty] → 源生成器会产出一个对应属性
                        //   私有字段 _fooBar       → 属性 FooBar
                        //   私有字段 fooBar / m_x  → 属性 FooBar / X
                        var hasObservable = field.AttributeLists
                            .SelectMany(a => a.Attributes)
                            .Any(a => a.Name.ToString().Contains("ObservableProperty"));
                        if (!hasObservable) break;

                        foreach (var v in field.Declaration.Variables)
                        {
                            var fieldName = v.Identifier.ValueText;
                            var propName = ToPropertyName(fieldName);
                            if (propName.Length > 0)
                            {
                                info.Properties.Add(propName);
                                info.GeneratedFromFields.Add(propName);
                            }
                        }
                        break;
                    }
                }
            }
        }
    }

    /// <summary>字段名 → [ObservableProperty] 生成的属性名（去掉下划线/前缀并首字母大写）。</summary>
    public static string ToPropertyName(string fieldName)
    {
        var s = fieldName.TrimStart('_');
        if (s.StartsWith("m_", StringComparison.Ordinal)) s = s[2..];
        if (s.Length == 0) return "";
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    public TypeInfo? Find(string typeName) => _types.TryGetValue(typeName, out var t) ? t : null;

    /// <summary>该类型（含已知基类）是否有这个可绑定成员。</summary>
    public bool HasProperty(string typeName, string propertyName)
    {
        if (!_types.TryGetValue(typeName, out var t)) return false;
        if (t.Properties.Contains(propertyName)) return true;

        // 基类：BaseViewModel / ObservableObject 之类的自有基类也要看
        return false;
    }

    /// <summary>
    /// 从 "IReadOnlyList&lt;Foo&gt;" / "ObservableCollection&lt;Foo&gt;" 这类类型文本里取出元素类型名。
    /// 取不到返回 null —— 调用方据此放弃检查，而不是猜一个可能错的类型。
    /// </summary>
    public static string? ElementTypeOf(string? propertyTypeText)
    {
        if (string.IsNullOrWhiteSpace(propertyTypeText)) return null;
        var open = propertyTypeText.IndexOf('<');
        var close = propertyTypeText.LastIndexOf('>');
        if (open < 0 || close <= open) return null;

        var inner = propertyTypeText[(open + 1)..close];
        // 嵌套泛型（例如 IReadOnlyList<(string, IReadOnlyList<X>)>）会让推断不可靠 → 放弃
        if (inner.Contains('<') || inner.Contains(',')) return null;

        var name = inner.Trim().TrimEnd('?');
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[(lastDot + 1)..] : name;
    }
}
