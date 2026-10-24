// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIconLoader.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. 原版的 SvgIconKey 是 SvgIconLoader 的私有嵌套类型，这里提取为
//      public 独立类型，便于单独测试「图标标识 → 资源路径」这一步的安全校验；
//   3. 原版的路径安全校验（拒绝 ..、绝对路径、非法字符）完整保留 ——
//      图标标识会拼进 pack URI，校验不到位就是路径穿越；
//      并额外把原版的 Trim('/') 改成显式拒绝绝对路径与空图标名
//      （原版会把 "material/" 静默当成 "material"）。

namespace UMango.Core.UI.Svg;

/// <summary>
/// 一个 SVG 图标的标识：图标包名 + 图标名。
/// </summary>
public readonly record struct SvgIconKey(string Pack, string Name)
{
    /// <summary>默认图标包名。</summary>
    public const string DefaultIconPack = "default";

    /// <summary>
    /// 解析图标标识。
    ///
    /// 接受 <c>pack/name</c> 与 <c>name</c>（后者用默认包）；
    /// 也接受带 <c>.svg</c> 后缀的写法。
    /// </summary>
    public static SvgIconKey? TryParse(string? icon, string defaultPack = DefaultIconPack)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return null;

        var normalized = icon.Trim().Replace('\\', '/');
        if (normalized.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        // 绝对路径与「包名/」这种空图标名一律拒绝。
        // PCL 原版在这里做 Trim('/')，会把 "material/" 静默当成 "material"、
        // 把 "/absolute" 静默当成 "absolute" —— 写错标识时不会报错，
        // 而是悄悄显示成另一个图标。这里改成显式拒绝。
        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || normalized.EndsWith('/')
            || normalized.Contains("..", StringComparison.Ordinal))
            return null;

        var parts = normalized.Split('/', 2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pack = parts.Length == 2 ? parts[0] : defaultPack;
        var name = parts.Length == 2 ? parts[1] : parts[0];

        if (!IsSafeResourcePath(pack) || !IsSafeResourcePath(name))
            return null;

        return new SvgIconKey(pack, name);
    }

    /// <summary>资源相对路径（相对于图标包目录）。</summary>
    public string RelativePath => $"{Pack}/{Name}.svg";

    public override string ToString() => $"{Pack}/{Name}";

    private static bool IsSafeResourcePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.EndsWith('/'))
            return false;

        return value.Split('/', StringSplitOptions.RemoveEmptyEntries).All(IsSafeResourceSegment);
    }

    private static bool IsSafeResourceSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
            return false;

        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}
