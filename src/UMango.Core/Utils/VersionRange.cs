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

        if (r.Length == 0 || r == "*") return v => true;

        // ~1.2.3 → ≥1.2.3, <1.3.0
        if (r.StartsWith('~'))
        {
            var ver = SemVer.TryParse(r[1..]) ?? throw new ArgumentException($"无法解析版本范围：{range}", nameof(range));
            var upper = new SemVer($"{ver.Major}.{ver.Minor + 1}.0");
            return v => v >= ver && v < upper;
        }

        // ^1.2.3 → ≥1.2.3, <2.0.0
        //
        // 但主版本为 0 时规则不同（npm 的约定）：
        //   ^0.2.3 → ≥0.2.3, <0.3.0   （只允许补丁级变动）
        //   ^0.0.3 → ≥0.0.3, <0.0.4   （锁死补丁）
        // 直接用 <(1+1).0.0 会把 0.x 的包全部放行。
        if (r.StartsWith('^'))
        {
            var ver = SemVer.TryParse(r[1..]) ?? throw new ArgumentException($"无法解析版本范围：{range}", nameof(range));
            SemVer upper;
            if (ver.Major != 0)
                upper = new SemVer($"{ver.Major + 1}.0.0");
            else if (ver.Minor != 0)
                upper = new SemVer($"0.{ver.Minor + 1}.0");
            else
                upper = new SemVer($"0.0.{ver.Patch + 1}");
            return v => v >= ver && v < upper;
        }

        // >=X.Y.Z / >X.Y.Z / <=X.Y.Z / <X.Y.Z
        var m = Regex.Match(r, @"^(>=|>|<=|<)\s*(.+)$");
        if (m.Success)
        {
            var op = m.Groups[1].Value;
            var ver = SemVer.TryParse(m.Groups[2].Value.Trim())
                      ?? throw new ArgumentException($"无法解析版本范围：{range}", nameof(range));
            return op switch
            {
                ">=" => v => v >= ver,
                ">" => v => v > ver,
                "<=" => v => v <= ver,
                "<" => v => v < ver,
                _ => v => true
            };
        }

        // 带通配符的精确匹配：1.2.x / 1.x / 1.2.*
        // 缺省段视为「任意」，且**最后一段是通配符时，其后的段也一律忽略**
        // （1.2.x.3 不该被当成有效范围）。
        if (r.Contains('x') || r.Contains('X') || r.Contains('*'))
        {
            var segments = r.Split('.');
            var concrete = new List<int>();
            var wildcardAt = -1;
            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                if (seg is "x" or "X" or "*")
                {
                    wildcardAt = i;
                    break;
                }
                if (!int.TryParse(seg, out var num))
                    throw new ArgumentException($"无法解析版本范围：{range}", nameof(range));
                concrete.Add(num);
            }

            return v =>
            {
                if (concrete.Count > 0 && v.Major != concrete[0]) return false;
                if (concrete.Count > 1 && v.Minor != concrete[1]) return false;
                if (concrete.Count > 2 && v.Patch != concrete[2]) return false;
                // 出现通配符之后，剩下的段不做要求。
                _ = wildcardAt;
                return true;
            };
        }

        // 精确匹配
        var exact = SemVer.TryParse(r) ?? throw new ArgumentException($"无法解析版本范围：{range}", nameof(range));
        return v => v.Major == exact.Major
                    && v.Minor == exact.Minor
                    && v.Patch == exact.Patch
                    && string.Equals(v.PreRelease, exact.PreRelease, StringComparison.Ordinal);
    }
}
