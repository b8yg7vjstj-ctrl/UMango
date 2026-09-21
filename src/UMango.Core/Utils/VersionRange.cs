using System.Text.RegularExpressions;

namespace UMango.Core.Utils;

/// <summary>
/// 版本范围匹配器。
///
/// 对应 PCL-CE 的 Utils/VersionRange.cs（Apache-2.0）。
/// 支持常见的版本范围格式：
///   · "1.2.3" → 精确匹配
///   · ">=1.0.0" / ">1.0.0" / "<=2.0.0" / "<2.0.0"
///   · "^1.2.3"（caret）→ 兼容 1.x.x（≥1.2.3, <2.0.0）
///   · "~1.2.3"（tilde）→ 兼容 1.2.x（≥1.2.3, <1.3.0）
///   · "1.2.x" → 精确匹配 1.2.x（x 是通配符）
///
/// 使用场景：package.json 的 dependencies 字段（"express": "^4.18.0"）、
/// npm view 的 version range 等。
/// </summary>
public sealed class VersionRange
{
    private readonly string _original;
    private readonly Func<SemVer, bool> _predicate;

    public VersionRange(string range)
    {
        _original = range;
        _predicate = Parse(range);
    }

    /// <summary>给定版本是否匹配此范围。</summary>
    public bool Matches(string version) => SemVer.TryParse(version) is { } v && _predicate(v);
    public bool Matches(SemVer version) => _predicate(version);
    public override string ToString() => _original;

    private static Func<SemVer, bool> Parse(string range)
    {
        var r = range.Trim();

        if (r == "*") return v => true;

        // ~1.2.3 → ≥1.2.3, <1.3.0
        if (r.StartsWith('~'))
        {
            var ver = new SemVer(r[1..]);
            return v => v >= new SemVer(ver.Major + "." + ver.Minor + ".0")
                      && v < new SemVer(ver.Major + "." + (ver.Minor + 1) + ".0");
        }

        // ^1.2.3 → ≥1.2.3, <2.0.0（保持主版本不变）
        if (r.StartsWith('^'))
        {
            var ver = new SemVer(r[1..]);
            return v => v >= ver && v < new SemVer((ver.Major + 1) + ".0.0");
        }

        // >=X.Y.Z / >X.Y.Z / <=X.Y.Z / <X.Y.Z
        var m = Regex.Match(r, @"^(>=|>|<=|<)\s*(.+)$");
        if (m.Success)
        {
            var op = m.Groups[1].Value;
            var ver = new SemVer(m.Groups[2].Value);
            return op switch
            {
                ">=" => v => v >= ver,
                ">" => v => v > ver,
                "<=" => v => v <= ver,
                "<" => v => v < ver,
                _ => v => true
            };
        }

        // 精确匹配（支持 1.2.x 格式的部分通配）
        if (r.Contains('x') || r.Contains('X'))
        {
            var segments = r.Split('.');
            return v => {
                if (segments.Length > 1 && int.TryParse(segments[0], out var m) && v.Major != m) return false;
                if (segments.Length > 2 && int.TryParse(segments[1], out var mi) && v.Minor != mi) return false;
                if (segments.Length > 3 && (segments[3] is "x" or "X" or "*")) return true;
                return true;
            };
        }

        // 精确匹配
        return v => v.Original == r || v.ToString() == r;
    }
}
