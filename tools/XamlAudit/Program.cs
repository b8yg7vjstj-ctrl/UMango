namespace UMango.Tools.XamlAudit;

/// <summary>
/// XAML 静态审计入口。
///
/// 用法：xaml-audit [UMango.App 工程目录]
/// 退出码：0 = 没有 ERROR，1 = 有 ERROR（可直接用于 CI 或本地脚本）
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var appRoot = args.Length > 0 ? args[0] : FindAppRoot();
        if (appRoot is null || !Directory.Exists(appRoot))
        {
            Console.Error.WriteLine("找不到 UMango.App 工程目录；请把路径作为第一个参数传入。");
            return 2;
        }

        // 诊断开关：打印每个文件收集到的资源定义与模板引用。
        // 工具的解析逻辑本身也需要能被看见 —— 否则"查不到"和"真不存在"分不清。
        if (args.Contains("--dump-keys"))
        {
            var code0 = CSharpIndex.Build([appRoot, Path.Combine(appRoot, "..", "UMango.Core")]);
            foreach (var f in XamlIndex.LoadAll(appRoot, code0))
            {
                Console.WriteLine($"{f.RelativePath}: 定义 {f.DefinedKeys.Count} 个键");
                foreach (var k in f.DefinedKeys.OrderBy(x => x, StringComparer.Ordinal))
                    Console.WriteLine($"    {k}");
                if (f.TemplateElementTypes.Count > 0)
                {
                    Console.WriteLine($"  模板键 → 元素类型：");
                    foreach (var (k, t) in f.TemplateElementTypes) Console.WriteLine($"    {k} → {t}");
                }
            }
            return 0;
        }

        Console.WriteLine($"XAML 审计：{appRoot}");
        Console.WriteLine(new string('─', 78));

        var audit = new Audit(appRoot);
        audit.Run();

        var findings = audit.Findings
            .OrderByDescending(f => f.Severity == "ERROR")
            .ThenBy(f => f.Category, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

        if (findings.Count == 0)
        {
            Console.WriteLine("未发现问题。");
        }
        else
        {
            string? lastCategory = null;
            foreach (var f in findings)
            {
                if (f.Category != lastCategory)
                {
                    Console.WriteLine();
                    Console.WriteLine($"◆ {f.Category}");
                    lastCategory = f.Category;
                }
                Console.WriteLine("   " + f);
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 78));
        Console.WriteLine($"合计：{findings.Count} 条（ERROR {audit.Errors}，WARN {audit.Warnings}）");

        // WARN 不导致失败：它们依赖启发式判断，可能有误报；
        // ERROR 是确定性结论（引用不存在的键/属性/方法），必须挡住。
        return audit.Errors > 0 ? 1 : 0;
    }

    /// <summary>从当前工作目录向上找 src/UMango.App。</summary>
    private static string? FindAppRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "UMango.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
