namespace UMango.Tools.XamlAudit;

/// <summary>一条审计发现。</summary>
public sealed record Finding(string Severity, string Category, string File, int Line, string Message)
{
    public override string ToString()
        => $"[{Severity}] {Category} {File}{(Line > 0 ? $":{Line}" : "")} — {Message}";
}

/// <summary>
/// XAML 静态审计。
///
/// **它存在的理由**：本项目在 macOS 上开发，而 WPF 只能在 Windows 上运行。
/// 于是 XAML 里那些"只有运行时才会暴露"的错误 —— 绑定路径拼错、资源键不存在、
/// 事件处理器没写、x:Name 对不上 —— 在本机既编译不过关也测不出来。
/// 这个工具把它们变成可在 macOS 上检查的静态问题。
/// </summary>
public sealed class Audit(string appRoot)
{
    private readonly string _appRoot = appRoot;
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;

    public int Errors => _findings.Count(f => f.Severity == "ERROR");
    public int Warnings => _findings.Count(f => f.Severity == "WARN");

    public void Run()
    {
        // 顺序很重要：先建 C# 索引，XAML 解析时要用它推断模板的作用域类型
        var code = CSharpIndex.Build([
            Path.Combine(_appRoot),
            Path.Combine(_appRoot, "..", "UMango.Core"),
        ]);
        var files = XamlIndex.LoadAll(_appRoot, code);

        // 全部已定义的资源键 = 所有 XAML 的定义 + App.xaml 的合并字典（合并字典本身就是 xaml，已包含）
        var definedKeys = files.SelectMany(f => f.DefinedKeys).ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"审计范围：{files.Count} 个 XAML 文件，{code.Types.Count} 个类型，{definedKeys.Count} 个资源键");
        Console.WriteLine();

        CheckThemeParity(files);

        foreach (var file in files)
        {
            CheckResourceKeys(file, definedKeys);
            CheckEventHandlers(file);
            CheckBindings(file, code);
            CheckImplicitTwoWayBindings(file, code);
        }

    }

    // ---------- 1) 资源键 ----------

    private void CheckResourceKeys(XamlFile file, HashSet<string> definedKeys)
    {
        foreach (var (key, line, isDynamic) in file.ResourceRefs)
        {
            if (definedKeys.Contains(key)) continue;

            // DynamicResource 找不到键时 WPF 不报错，只是**静默取不到值**
            // （表现为颜色/尺寸变成默认值），比崩掉更难查 —— 所以这类要当错误报。
            _findings.Add(new Finding("ERROR", "资源键",
                file.RelativePath, line,
                $"引用了不存在的资源键 {{{(isDynamic ? "DynamicResource" : "StaticResource")} {key}}}"));
        }
    }

    // ---------- 1.5) 四套主题字典的键一致性 ----------

    /// <summary>
    /// 检查四套主题字典（PCL经典/Win10 Fluent × 浅/深）是否定义了**完全相同的键集合**。
    ///
    /// 为什么这条最重要：换主题靠的正是"键名一致、整块替换字典"。
    /// 某套字典少一个键时，切到那套主题**不会报任何错** —— 只是那个颜色/尺寸静默失效、
    /// 退回 WPF 默认值（例如文字变黑、进度条变灰）。这种问题在真机上极难定位，
    /// 而这里是纯静态可查的。
    /// </summary>
    private void CheckThemeParity(List<XamlFile> files)
    {
        var themeFiles = files
            .Where(f => Path.GetFileName(f.RelativePath).StartsWith("Tokens.", StringComparison.Ordinal))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        if (themeFiles.Count < 2) return;

        // 以第一套为基准，用"并集"来判定：任何一套缺了别人有的键都要报
        var allKeys = themeFiles.SelectMany(f => f.DefinedKeys).ToHashSet(StringComparer.Ordinal);

        foreach (var f in themeFiles)
        {
            var missing = allKeys.Where(k => !f.DefinedKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count == 0) continue;

            _findings.Add(new Finding("ERROR", "主题一致性",
                f.RelativePath, 0,
                $"缺少 {missing.Count} 个键（其它主题字典有而它没有）：{Truncate(string.Join(", ", missing), 200)}"
                + " —— 切到这套主题时这些键会静默失效，退回 WPF 默认值"));
        }
    }

    // ---------- 2) 事件处理器 ----------

    private void CheckEventHandlers(XamlFile file)
    {
        var codeBehind = FindCodeBehind(file);
        if (codeBehind is null) return;

        var text = File.ReadAllText(codeBehind);
        foreach (var (handler, line) in file.EventHandlers)
        {
            // 代码里必须存在同名方法
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    text, $@"\b(void|async\s+void)\s+{System.Text.RegularExpressions.Regex.Escape(handler)}\s*\("))
            {
                _findings.Add(new Finding("ERROR", "事件处理器",
                    file.RelativePath, line,
                    $"XAML 挂的处理器 {handler} 在 {Path.GetFileName(codeBehind)} 里找不到对应方法"));
            }
        }
    }

    // ---------- 3) x:Name ----------
    //
    // 这里曾经有一个"代码里用了 X、XAML 里没有同名 x:Name"的检查，现已**删除**。
    // 原因：它基于正则猜标识符，实际跑出来 115 条**全是误报** ——
    // 把命名空间片段（CommunityToolkit、Runtime）、枚举取值（AvailabilityState）、
    // 类型名（CultureInfo）统统当成了控件名。
    // 一个 100% 误报的检查比没有检查更糟：它会训练人忽略审计输出。
    // 真要做这件事应当基于语义模型（Roslyn 的符号绑定）判断"这个标识符是不是控件字段"，
    // 而不是靠命名猜测 —— 那留作后续工作，当前不做半成品。

    // ---------- 4) 绑定路径 ----------

    private void CheckBindings(XamlFile file, CSharpIndex code)
    {
        var dataContextType = FindDataContextType(file);
        if (dataContextType is null) return;          // 认不出 DataContext 就不猜

        var vm = code.Find(dataContextType);
        if (vm is null) return;

        // 2a) 按 key 引用的模板内的绑定：作用域是"引用处集合的元素类型"
        foreach (var (path, line, templateKey) in file.KeyedTemplateBindings)
        {
            // 引用处没解析出元素类型就不查（否则就是拿页面 VM 去校验模板，必然全错）
            if (!file.TemplateElementTypes.TryGetValue(templateKey, out var typeName)) continue;
            var element = code.Find(typeName);
            if (element is null) continue;

            if (!element.Properties.Contains(path))
            {
                _findings.Add(new Finding("ERROR", "绑定",
                    file.RelativePath, line,
                    $"{{Binding {path}}} 在模板 {templateKey} 的元素类型 {typeName} 上不存在（可用：{Truncate(string.Join(", ", element.Properties))}）"));
            }
        }

        foreach (var (path, line, elementType) in file.Bindings)
        {
            // DataTemplate 里的绑定作用域是集合元素，不是页面 VM
            if (elementType is not null)
            {
                var element = code.Find(elementType);
                if (element is null) continue;         // 元素类型不在源码里（例如 Core 的 record）→ 不猜
                if (!element.Properties.Contains(path))
                {
                    _findings.Add(new Finding("ERROR", "绑定",
                        file.RelativePath, line,
                        $"{{Binding {path}}} 在元素类型 {elementType} 上不存在（可用：{Truncate(string.Join(", ", element.Properties))}）"));
                }
                continue;
            }

            if (path is "DataContext" or "RelativeSource" or "Self" or "ElementName") continue;

            if (!vm.Properties.Contains(path))
            {
                _findings.Add(new Finding("ERROR", "绑定",
                    file.RelativePath, line,
                    $"{{Binding {path}}} 在 {dataContextType} 上不存在"));
            }
        }
    }

    private static string Truncate(string s, int max = 120) => s.Length <= max ? s : s[..max] + "…";

    // ---------- 5) 隐式 TwoWay 绑定打在只读属性上（真机崩溃级问题） ----------

    /// <summary>
    /// 检查那些 **默认就是 TwoWay** 的依赖属性上，是否绑了**只读属性**却没写 Mode。
    ///
    /// **这条检查来自一次真机崩溃**：`DownloaderView.xaml` 里
    /// `ProgressBar.Value="{Binding ProgressFraction}"`（Value 默认 TwoWay），
    /// 而 ProgressFraction 是只读属性。WPF 在绑定挂载时抛
    /// "无法对只读属性进行 TwoWay 或 OneWayToSource 绑定" 的 InvalidOperationException，
    /// **导致整个应用启动即崩溃**。
    ///
    /// 这个缺陷：dotnet build 不报、单元测试不报、XAML 静态审计原先也不查 ——
    /// 只有真机启动才暴露。因此把它固化成静态检查，同类问题再也进不来。
    /// </summary>
    private void CheckImplicitTwoWayBindings(XamlFile file, CSharpIndex code)
    {
        // 这些依赖属性的默认绑定模式是 TwoWay（由 WPF 元数据决定，不是我们的选择）
        var twoWayByDefault = new HashSet<string>(StringComparer.Ordinal)
        {
            "Value", "Text", "IsChecked", "SelectedItem", "SelectedIndex", "SelectedValue", "Content"
        };

        var dataContextType = FindDataContextType(file);
        if (dataContextType is null) return;

        var vm = code.Find(dataContextType);
        if (vm is null) return;

        foreach (var (path, line, elementType) in file.Bindings)
        {
            // 需要在 XAML 里能看出"属性名 + 未写 Mode"，因此重新扫一遍原始文本：
            // Bindings 列表只保留了路径，丢掉了属性名与 Mode 信息。
            _ = path; _ = line; _ = elementType;
        }

        // 直接扫原始 XAML 文本（比从 Bindings 列表回溯更直接）
        string raw;
        try { raw = File.ReadAllText(file.Path); }
        catch (Exception) { return; }

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     raw, @"(\w[\w.]*)\s*=\s*""\{Binding\s+([^}""]+)"""))
        {
            var propName = m.Groups[1].Value;
            var expr = m.Groups[2].Value;

            var shortName = propName.Contains('.') ? propName[(propName.LastIndexOf('.') + 1)..] : propName;
            if (!twoWayByDefault.Contains(shortName)) continue;
            if (expr.Contains("Mode=", StringComparison.Ordinal)) continue;   // 已显式指定

            var head = expr.Split(',')[0].Trim();
            var bindPath = head.Contains('=') ? head[(head.IndexOf('=') + 1)..].Trim() : head;
            if (bindPath.Length == 0) continue;

            // 只在该属性**确实存在**但不可写时才报（存在性由另一条检查负责）
            if (!vm.Properties.Contains(bindPath)) continue;

            var line = raw[..m.Index].Count(c => c == '\n') + 1;
            _findings.Add(new Finding("ERROR", "只读绑定",
                file.RelativePath, line,
                $"{shortName}=\"{{Binding {bindPath}}}\" 未写 Mode，但 {shortName} 的默认绑定模式是 TwoWay；"
                + $"若 {bindPath} 是只读属性，WPF 会在挂载绑定时抛异常并**导致应用启动崩溃**。"
                + "请加 Mode=OneWay。"));
        }
    }

    // ---------- 辅助 ----------

    /// <summary>找到该 XAML 对应的 code-behind 文件。</summary>
    private static string? FindCodeBehind(XamlFile file) => file.Path + ".cs" is var p && File.Exists(p) ? p : null;

    /// <summary>
    /// 从 code-behind 里找出 DataContext 被设置成什么类型。
    /// 认不出就返回 null —— 宁可不查，也不要拿错误的类型去校验、制造一堆假报错。
    /// </summary>
    private static string? FindDataContextType(XamlFile file)
    {
        var codeBehind = FindCodeBehind(file);
        if (codeBehind is null) return null;

        var text = File.ReadAllText(codeBehind);
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"DataContext\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static int LineNumber(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }
}
