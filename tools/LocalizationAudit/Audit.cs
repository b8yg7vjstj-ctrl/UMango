namespace UMango.Tools.LocalizationAudit;

/// <summary>一条审计发现。</summary>
public sealed record Finding(string Severity, string Category, string File, int Line, string Message)
{
    public override string ToString()
        => $"[{Severity}] {Category} {File}{(Line > 0 ? $":{Line}" : "")} — {Message}";
}

/// <summary>
/// 本地化占位符元数审计。
///
/// <para>
/// <b>它拦的是什么</b>：文案模板里的占位符个数与调用点传的实参个数对不上。
/// 对不上有两种后果：
/// <list type="number">
///   <item>实参**少于**占位符 → <c>string.Format</c> 抛 <c>FormatException</c>；</item>
///   <item>实参**多于**占位符 → 多出来的被静默丢弃，界面上少一段信息。</item>
/// </list>
///
/// <para>
/// <b>为什么它必须存在</b>：这类缺陷编译期不报、单元测试也不报
/// （测试通常用的是 KeyOnlyTextProvider，拿不到真实模板），
/// 只有真机跑到那一行才炸 —— 实际后果是 Avalonia 壳**启动即崩溃**
/// （诊断页在构造函数里取文案）。
/// </para>
///
/// <para>
/// <b>两条检查</b>：
/// <list type="bullet">
///   <item>① <b>语言间占位符漂移</b>：同一个键在 zh-CN 与 en-US 里的占位符集合必须一致。
///     不一致时，某个语言下会崩、另一个语言下会少字 —— 而且只在切语言后才暴露。</item>
///   <item>② <b>调用点元数</b>：调用点传的实参个数必须 ≥ 模板需要的占位符个数。</item>
/// </list>
/// </para>
/// </summary>
public sealed class Audit(string repoRoot)
{
    private readonly string _repoRoot = repoRoot;
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;
    public int Errors => _findings.Count(f => f.Severity == "ERROR");
    public int Warnings => _findings.Count(f => f.Severity == "WARN");

    public void Run()
    {
        var languagesDir = Path.Combine(_repoRoot, "src", "UMango.Shell", "Localization", "Languages");
        var catalogs = LoadCatalogs(languagesDir);
        if (catalogs.Count == 0)
        {
            _findings.Add(new Finding("ERROR", "语言文件", "src/UMango.Shell/Localization/Languages", 0,
                "没找到任何语言文件"));
            return;
        }

        Console.WriteLine($"语言文件：{string.Join("、", catalogs.Select(c => $"{c.Key}（{c.Value.Count} 键）"))}");
        Console.WriteLine();

        CheckPlaceholderDrift(catalogs);
        CheckCallSiteArity(catalogs);
    }

    /// <summary>语言代码 → 该语言的 键 → 模板。</summary>
    private Dictionary<string, Dictionary<string, string>> LoadCatalogs(string languagesDir)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!Directory.Exists(languagesDir)) return result;

        foreach (var file in Directory.EnumerateFiles(languagesDir, "*.txt"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                result[code] = LanguageFiles.Parse(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                _findings.Add(new Finding("ERROR", "语言文件", file, 0, $"读取失败：{ex.Message}"));
            }
        }
        return result;
    }

    // ---------- ① 语言间占位符漂移 ----------

    /// <summary>
    /// 同一个键在不同语言里的占位符集合必须一致。
    ///
    /// <para>
    /// 为什么这条是硬要求：文案是**分层叠加**的 —— 兜底层（zh-CN）始终加载，
    /// 叠加层（en-US）只写差异键。于是同一个键的模板在切换语言时会被整个换掉，
    /// 换掉的模板必须与兜底层**占位符个数相同**，否则切换语言的那一瞬间
    /// 就会抛 <c>FormatException</c>（或静默丢字）。
    /// </para>
    ///
    /// <para>
    /// 这条曾真的漏过一个：<c>Download.Empty.NoInstalled</c> 在中文里是
    /// <c>{0} 下没有已安装的{1}</c>（2 个占位符），英文里只有 <c>{0}</c>（1 个）。
    /// 中文界面一切正常，切到英文就少一段。
    /// </para>
    /// </summary>
    private void CheckPlaceholderDrift(Dictionary<string, Dictionary<string, string>> catalogs)
    {
        var fallbackCode = "zh-CN";
        if (!catalogs.TryGetValue(fallbackCode, out var fallback)) return;

        foreach (var (code, entries) in catalogs)
        {
            if (code == fallbackCode) continue;

            foreach (var (key, template) in entries)
            {
                if (!fallback.TryGetValue(key, out var fallbackTemplate)) continue;

                var expected = LanguageFiles.PlaceholderIndices(fallbackTemplate);
                var actual = LanguageFiles.PlaceholderIndices(template);
                if (expected.SequenceEqual(actual)) continue;

                _findings.Add(new Finding("ERROR", "占位符漂移", $"Languages/{code}.txt", 0,
                    $"键 {key} 的占位符与 {fallbackCode} 不一致："
                    + $"{fallbackCode}={expected.Count} 个（{Format(expected)}），"
                    + $"{code}={actual.Count} 个（{Format(actual)}）"
                    + " —— 切到该语言时调用点会抛 FormatException 或静默丢字"));
            }
        }
    }

    private static string Format(IReadOnlyList<int> indices)
        => indices.Count == 0 ? "无" : string.Join(",", indices.Select(i => $"{{{i}}}"));

    // ---------- ② 调用点元数 ----------

    /// <summary>
    /// 调用点传的实参个数必须覆盖模板需要的占位符个数。
    /// </summary>
    private void CheckCallSiteArity(Dictionary<string, Dictionary<string, string>> catalogs)
    {
        var merged = MergeCatalogs(catalogs);

        var sourceRoots = new[]
        {
            Path.Combine(_repoRoot, "src", "UMango.Core"),
            Path.Combine(_repoRoot, "src", "UMango.Shell"),
            Path.Combine(_repoRoot, "src", "UMango.App"),
            Path.Combine(_repoRoot, "src", "UMango.Avalonia"),
        };

        var callSites = CallSiteScanner.Scan(sourceRoots, _repoRoot);
        Console.WriteLine($"扫描到 {callSites.Count} 个文案调用点");
        Console.WriteLine();

        foreach (var site in callSites)
        {
            if (site.ArityUnknown) continue;

            if (!merged.TryGetValue(site.Key, out var template))
            {
                // 键在任何语言里都没有 → 界面上会直接显示键名本身
                _findings.Add(new Finding("WARN", "文案键", site.File, site.Line,
                    $"键 {site.Key} 在任何语言文件里都不存在（界面会显示键名本身）"));
                continue;
            }

            var required = LanguageFiles.PlaceholderIndices(template);
            var needed = required.Count == 0 ? 0 : required.Max() + 1;

            if (site.ProvidedArgs < needed)
            {
                _findings.Add(new Finding("ERROR", "占位符元数", site.File, site.Line,
                    $"键 {site.Key} 的模板需要 {needed} 个实参（{Format(required)}），"
                    + $"调用点只给了 {site.ProvidedArgs} 个 —— string.Format 会抛 FormatException"));
            }
            else if (site.ProvidedArgs > needed)
            {
                _findings.Add(new Finding("WARN", "占位符元数", site.File, site.Line,
                    $"键 {site.Key} 的模板只需要 {needed} 个实参（{Format(required)}），"
                    + $"调用点给了 {site.ProvidedArgs} 个 —— 多出来的会被静默丢弃"));
            }
        }
    }

    /// <summary>合并各语言目录（叠加层覆盖兜底层），得到最终可见的键 → 模板。</summary>
    private static Dictionary<string, string> MergeCatalogs(Dictionary<string, Dictionary<string, string>> catalogs)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entries in catalogs.Values)
        {
            foreach (var (key, template) in entries) merged[key] = template;
        }
        return merged;
    }
}
