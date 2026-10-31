namespace UMango.Tools.LocalizationAudit;

/// <summary>
/// 本地化占位符元数静态审计入口。
///
/// <para>
/// 用法：<c>localization-audit [仓库根目录]</c>
/// </para>
/// <para>
/// 退出码：0 = 没有 ERROR，1 = 有 ERROR（可直接用于 CI）。
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot();
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            Console.Error.WriteLine("找不到仓库根目录；请把路径作为第一个参数传入。");
            return 2;
        }

        Console.WriteLine($"本地化占位符审计：{repoRoot}");
        Console.WriteLine(new string('─', 78));

        var audit = new Audit(repoRoot);
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

        return audit.Errors > 0 ? 1 : 0;
    }

    /// <summary>从当前工作目录向上找含 <c>src/UMango.Shell</c> 的目录。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "UMango.Shell"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
