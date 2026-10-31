using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UMango.Tools.LocalizationAudit;

/// <summary>一个本地化调用点。</summary>
/// <param name="File">仓库相对路径。</param>
/// <param name="Line">行号（1 起）。</param>
/// <param name="Key">键名（字面量）。</param>
/// <param name="ProvidedArgs">实际提供的格式化实参个数。</param>
/// <param name="ArityUnknown">实参个数无法静态确定（例如展开了数组）。</param>
public sealed record CallSite(string File, int Line, string Key, int ProvidedArgs, bool ArityUnknown);

/// <summary>
/// 用 Roslyn **语法分析**找出 C# 里的本地化调用点。
///
/// <para>
/// 为什么是语法分析而不是语义分析：WPF 工程是 <c>net10.0-windows</c>，
/// 它的程序集在 macOS 上加载不了，做不了完整编译。而这里要认的调用点形状很固定，
/// 语法层面足以判定。
/// </para>
///
/// <para>
/// 认哪些形状（其余一律不认，避免误报）：
/// <list type="bullet">
///   <item><c>ShellText.L(...)</c> / <c>ViewText.L(...)</c>；</item>
///   <item><c>LocalizationService.Text(...)</c>；</item>
///   <item>类型内部自己声明的 <c>L(string, params object?[])</c> 辅助方法。</item>
/// </list>
/// 键必须是**字面量**才能静态判定占位符个数；动态拼出来的键一律跳过。
/// </para>
/// </summary>
public static class CallSiteScanner
{
    /// <summary>已知的外层文案入口（接收者简单名 → 方法名）。</summary>
    private static readonly Dictionary<string, string> KnownEntrypoints = new(StringComparer.Ordinal)
    {
        ["ShellText"] = "L",
        ["ViewText"] = "L",
        ["LocalizationService"] = "Text",
    };

    /// <summary>单参文案桥（只接受 key，不接受格式化实参）。</summary>
    private static readonly HashSet<string> KeyOnlyBridges = new(StringComparer.Ordinal)
    {
        "ShellLocalization", "WpfLocalizationBridge", "AvaloniaLocalizationBridge",
    };

    /// <summary>扫描若干源码根目录里的全部 .cs。</summary>
    public static List<CallSite> Scan(IEnumerable<string> sourceRoots, string repoRoot)
    {
        var result = new List<CallSite>();

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
                ScanFile(file, repoRoot, result);
            }
        }

        return result;
    }

    private static void ScanFile(string path, string repoRoot, List<CallSite> result)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception) { return; }

        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();
        var relative = Path.GetRelativePath(repoRoot, path);

        // 本文件里声明了 `L(string, params object?[])` 的类型 —— 它们的 L 是文案辅助方法
        var helperTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (DeclaresLocalizationHelper(typeDecl)) helperTypes.Add(typeDecl.Identifier.ValueText);
        }

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!TryGetLiteralKey(inv, out var key)) continue;
            if (!IsLocalizationCall(inv, helperTypes)) continue;

            var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            // 实参个数：调用点自己带的，或由外层 string.Format 提供的
            var args = inv.ArgumentList.Arguments;
            int provided;
            bool unknown = false;

            if (args.Count >= 2)
            {
                provided = args.Count - 1;
                // `L(key, someArray)` —— 数组展开，实参个数静态不可知
                if (args.Count == 2 && args[1].Expression.ToString().EndsWith("[]", StringComparison.Ordinal))
                {
                    unknown = true;
                }
            }
            else
            {
                // 只传了 key：占位符要么由外层 string.Format 填，要么就没人填
                var outer = OuterFormatArgs(inv);
                provided = outer is { } o ? o : 0;
            }

            result.Add(new CallSite(relative, line, key, provided, unknown));
        }
    }

    /// <summary>该类型里是否声明了 <c>string L(string key, params object?[] args)</c>。</summary>
    private static bool DeclaresLocalizationHelper(TypeDeclarationSyntax typeDecl)
    {
        foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText != "L") continue;
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count != 2) continue;
            if (parameters[0].Type?.ToString() != "string") continue;
            if (!parameters[1].Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword))) continue;
            return true;
        }
        return false;
    }

    /// <summary>调用点是不是文案调用；是的话把字面量键取出来。</summary>
    private static bool TryGetLiteralKey(InvocationExpressionSyntax inv, out string key)
    {
        key = string.Empty;
        var args = inv.ArgumentList.Arguments;
        if (args.Count == 0) return false;

        if (args[0].Expression is not LiteralExpressionSyntax literal) return false;
        if (literal.Token.Value is not string text) return false;

        key = text;
        return true;
    }

    private static bool IsLocalizationCall(InvocationExpressionSyntax inv, HashSet<string> helperTypes)
    {
        switch (inv.Expression)
        {
            case MemberAccessExpressionSyntax member:
            {
                var receiver = (member.Expression as IdentifierNameSyntax)?.Identifier.ValueText
                               ?? member.Expression.ToString();
                var method = member.Name.Identifier.ValueText;

                if (KnownEntrypoints.TryGetValue(receiver, out var expected) && method == expected)
                    return true;
                if (KeyOnlyBridges.Contains(receiver) && method == "Text")
                    return true;
                return false;
            }

            case IdentifierNameSyntax identifier:
            {
                // 裸 `L(...)`：只有当所在类型确实声明了文案辅助方法时才认
                if (identifier.Identifier.ValueText != "L") return false;
                var typeDecl = inv.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                return typeDecl is not null && helperTypes.Contains(typeDecl.Identifier.ValueText);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 若本次调用是外层 <c>string.Format(...)</c> 的格式串，返回外层提供的实参个数。
    /// </summary>
    private static int? OuterFormatArgs(InvocationExpressionSyntax inv)
    {
        if (inv.Parent is not ArgumentSyntax argument) return null;
        if (argument.Parent is not ArgumentListSyntax argumentList) return null;
        if (argumentList.Parent is not InvocationExpressionSyntax outer) return null;

        // 必须是外层调用的**第一个**实参（格式串位置）
        if (argumentList.Arguments.Count == 0 || !ReferenceEquals(argumentList.Arguments[0], argument))
            return null;

        var methodName = outer.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
        if (methodName is not ("Format" or "string.Format")) return null;

        var outerArgs = outer.ArgumentList?.Arguments.Count ?? 0;
        return Math.Max(0, outerArgs - 1);
    }
}
