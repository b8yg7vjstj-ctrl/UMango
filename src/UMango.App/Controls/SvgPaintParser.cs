// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgPaintParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.App.Controls；
//   2. 补了**关键字颜色名**（原版只认 #rgb/#rrggbb 与 none，
//      SVG 规范里 "red"、"black"、"white" 等都是合法颜色值）；
//   3. 补了 **rgb()/rgba()** 形式（SVG 1.1 允许的 CSS 颜色语法之一）。

using System.Globalization;
using System.Windows.Media;

namespace UMango.App.Controls;

/// <summary>解析 SVG 里的颜色写法。</summary>
public static class SvgPaintParser
{
    /// <summary>
    /// 解析颜色。无法识别时返回 fallback。
    /// </summary>
    public static Brush? ParseBrush(string? value, Brush? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var text = value.Trim();

        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.StartsWith('#'))
            return ParseHex(text) ?? fallback;

        if (text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
            return ParseRgb(text) ?? fallback;

        if (TryParseNamedColor(text, out var named))
            return named;

        return fallback;
    }

    private static Brush? ParseHex(string text)
    {
        var hex = text[1..];
        if (hex.Length is 3 or 4)
        {
            // #rgb / #rgba：每个字符重复一次
            var builder = new char[hex.Length * 2];
            for (var i = 0; i < hex.Length; i++)
            {
                builder[i * 2] = hex[i];
                builder[i * 2 + 1] = hex[i];
            }

            hex = new string(builder);
        }

        if (hex.Length is not (6 or 8))
            return null;

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;

        var a = hex.Length == 8 ? (byte)(value >> 24) : (byte)255;
        var r = (byte)(value >> 16);
        var g = (byte)(value >> 8);
        var b = (byte)value;
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private static Brush? ParseRgb(string text)
    {
        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open < 0 || close < 0 || close < open)
            return null;

        var parts = text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4))
            return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            return null;

        var a = 255;
        if (parts.Length == 4)
        {
            // alpha 可能是 0~1 的小数，也可能是 0~255 的整数
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                return null;
            a = alpha <= 1D ? (int)Math.Round(alpha * 255) : (int)Math.Round(alpha);
        }

        return new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(a, 0, 255),
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255)));
    }

    private static bool TryParseNamedColor(string text, out Brush brush)
    {
        brush = null!;
        var known = NamedColors.TryGetValue(text.ToLowerInvariant(), out var color);
        if (!known)
            return false;

        brush = new SolidColorBrush(color);
        return true;
    }

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.Ordinal)
    {
        ["black"] = Color.FromRgb(0, 0, 0),
        ["silver"] = Color.FromRgb(192, 192, 192),
        ["gray"] = Color.FromRgb(128, 128, 128),
        ["grey"] = Color.FromRgb(128, 128, 128),
        ["white"] = Color.FromRgb(255, 255, 255),
        ["maroon"] = Color.FromRgb(128, 0, 0),
        ["red"] = Color.FromRgb(255, 0, 0),
        ["purple"] = Color.FromRgb(128, 0, 128),
        ["fuchsia"] = Color.FromRgb(255, 0, 255),
        ["magenta"] = Color.FromRgb(255, 0, 255),
        ["green"] = Color.FromRgb(0, 128, 0),
        ["lime"] = Color.FromRgb(0, 255, 0),
        ["olive"] = Color.FromRgb(128, 128, 0),
        ["yellow"] = Color.FromRgb(255, 255, 0),
        ["navy"] = Color.FromRgb(0, 0, 128),
        ["blue"] = Color.FromRgb(0, 0, 255),
        ["teal"] = Color.FromRgb(0, 128, 128),
        ["aqua"] = Color.FromRgb(0, 255, 255),
        ["cyan"] = Color.FromRgb(0, 255, 255),
        ["orange"] = Color.FromRgb(255, 165, 0),
        ["transparent"] = Color.FromArgb(0, 0, 0, 0),
    };
}
