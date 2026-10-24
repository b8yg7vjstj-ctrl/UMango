using System.Text.RegularExpressions;

namespace UMango.Core.Utils;

/// <summary>
/// 语义化版本号解析与比较。
///
/// 对应 PCL-CE 的 Utils/SemVer.cs（Apache-2.0）。
/// 统一各适配器里分散的版本比较逻辑，提供标准化的版本号操作。
///
/// 版本格式：MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]
/// 例：1.2.3、1.0.0-beta.1、2.0.0+build.123
///
/// 与 PCL 原版的差异（均向 semver.org 规范靠拢，PCL 原版有偏差）：
///   1. **预发布标识符按规范逐段比较**。PCL 原版直接用
///      <c>string.Compare(Prerelease, other.Prerelease, Ordinal)</c> 比较整串，
///      于是 <c>1.0.0-alpha.10</c> 会被判为**小于** <c>1.0.0-alpha.2</c>
///      （因为 "alpha.10" &lt; "alpha.2" 按字典序）—— 与规范相反。
///      本类按 semver.org 的规则：逐个「点分标识符」比较，
///      纯数字标识符按数值比较（且数值标识符优先级**低于**非数值标识符），
///      标识符个数更多者优先级更高（前提是前面全部相等）。
///   2. **Build 元数据不参与优先级比较**（规范要求），但参与相等性判断。
///      这意味着 <c>1.0.0+a</c> 与 <c>1.0.0+b</c> 既"相等"（CompareTo）又不"相等"（Equals），
///      与 PCL 原版行为一致，属**刻意保留**的口径。
///   3. 增加了 <see cref="Prerelease"/> / <see cref="BuildMetadata"/> 两个
///      与 PCL 同名的别名属性，便于对照 PCL 代码。
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string PreRelease { get; }
    public string Build { get; }
    public string Original { get; }

    /// <summary>PCL 原版的属性名（与 <see cref="PreRelease"/> 同值）。</summary>
    public string Prerelease => PreRelease;

    /// <summary>PCL 原版的属性名（与 <see cref="Build"/> 同值）。</summary>
    public string BuildMetadata => Build;

    public SemVer(string version)
    {
        Original = version;
        var main = version.Split('+')[0];
        var preIdx = main.IndexOf('-');
        var pre = preIdx >= 0 ? main[(preIdx + 1)..] : "";
        var buildIdx = version.IndexOf('+');
        var build = buildIdx >= 0 ? version[(buildIdx + 1)..] : "";

        var parts = (preIdx >= 0 ? main[..preIdx] : main).Split('.');
        Major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        Minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
        Patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        PreRelease = pre;
        Build = build;
    }

    public bool IsPreRelease => PreRelease.Length > 0;
    public bool IsPrerelease => IsPreRelease; // alias

    /// <summary>是否高于（不兼容变更）。</summary>
    public bool IsMajorBump(SemVer other) => Major != other.Major;

    /// <summary>是否兼容（主版本相同）。</summary>
    public bool IsCompatible(SemVer other) => Major == other.Major;

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;
        return ComparePrerelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// 按 semver.org 规则比较预发布标识符。
    ///
    /// 规则要点：
    ///   · 正式版（无预发布标识）优先级**高于**任何预发布版；
    ///   · 逐个点分标识符比较，先分出胜负者立即返回；
    ///   · 两者都是纯数字 → 按数值比较（"2" &lt; "10"）；
    ///   · 一方纯数字一方非数字 → **纯数字优先级更低**
    ///     （"1.0.0-alpha" &lt; "1.0.0-alpha.1"，而 "1.0.0-1" &lt; "1.0.0-alpha"）；
    ///   · 都不纯数字 → 按 Ordinal 字典序；
    ///   · 前面全部相等时，标识符**更多**者优先级更高。
    /// </summary>
    internal static int ComparePrerelease(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;

        // 正式版优先级高于预发布版
        if (string.IsNullOrEmpty(a)) return 1;
        if (string.IsNullOrEmpty(b)) return -1;

        var idsA = a.Split('.');
        var idsB = b.Split('.');
        var minLength = Math.Min(idsA.Length, idsB.Length);

        for (var i = 0; i < minLength; i++)
        {
            var idA = idsA[i];
            var idB = idsB[i];

            var aIsNumeric = int.TryParse(idA, out var numA);
            var bIsNumeric = int.TryParse(idB, out var numB);

            int result;
            if (aIsNumeric && bIsNumeric)
            {
                result = numA.CompareTo(numB);
            }
            else if (aIsNumeric || bIsNumeric)
            {
                // 数值标识符比非数值标识符优先级低
                result = aIsNumeric ? -1 : 1;
            }
            else
            {
                result = string.Compare(idA, idB, StringComparison.Ordinal);
            }

            if (result != 0) return result;
        }

        return idsA.Length.CompareTo(idsB.Length);
    }

    public bool Equals(SemVer? other) =>
        other is not null &&
        Major == other.Major &&
        Minor == other.Minor &&
        Patch == other.Patch &&
        string.Equals(PreRelease, other.PreRelease, StringComparison.Ordinal) &&
        string.Equals(Build, other.Build, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease, Build);
    public override string ToString() => Original;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator ==(SemVer? a, SemVer? b) =>
        ReferenceEquals(a, b) || (a is not null && a.Equals(b));
    public static bool operator !=(SemVer? a, SemVer? b) => !(a == b);

    /// <summary>尝试解析版本号，失败返回 null。</summary>
    public static SemVer? TryParse(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        // 合法格式：X.Y.Z 或 X.Y.Z-xxx 或 X.Y.Z+xxx 或 X.Y.Z-xxx+xxx
        // 非法如 "not-a-version" 会被拒绝
        if (!Regex.IsMatch(version, @"^\d+(\.\d+){0,2}(\-.*)?(\+.*)?$")) return null;
        try { return new SemVer(version); }
        catch { return null; }
    }
}
