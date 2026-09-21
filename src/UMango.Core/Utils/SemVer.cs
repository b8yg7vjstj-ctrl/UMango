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
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string PreRelease { get; }
    public string Build { get; }
    public string Original { get; }

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
        // 预发布版本低于同版本正式版
        if (IsPreRelease && !other.IsPreRelease) return -1;
        if (!IsPreRelease && other.IsPreRelease) return 1;
        return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
    public override string ToString() => Original;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

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
