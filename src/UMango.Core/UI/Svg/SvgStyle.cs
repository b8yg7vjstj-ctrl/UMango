// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIconStyle.cs 与 SvgNumberParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. 原版用 XElement 取属性，这里改为读平台中立的属性字典
//      （Core 不引用 System.Xml.Linq 之外的东西，但更重要的是让解析层
//      与「元素类型」解耦：App 层从 WPF/SVG 源读出属性后交给这里解析）；
//   3. 原版的 Merge 接受 XElement，这里接受属性字典。

using System.Globalization;
using System.Text.RegularExpressions;

namespace UMango.Core.UI.Svg;

/// <summary>SVG 元素的样式。</summary>
public sealed record SvgStyle
{
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public string? StrokeLineCap { get; init; }
    public string? StrokeLineJoin { get; init; }
    public string? FillRule { get; init; }
    public double? StrokeWidth { get; init; }
    public double Opacity { get; init; } = 1D;
    public double FillOpacity { get; init; } = 1D;
    public double StrokeOpacity { get; init; } = 1D;

    /// <summary>
    /// 合并子元素样式：子元素的直接属性优先于继承值，
    /// 子元素的 <c>style="…"</c> 内联样式优先于直接属性（与 CSS 层叠一致）。
    /// </summary>
    public SvgStyle Merge(IReadOnlyDictionary<string, string> attributes)
    {
        var inlineStyle = ParseStyle(Get(attributes, "style"));

        var opacity = Get(attributes, "opacity", inlineStyle);
        var fillOpacity = Get(attributes, "fill-opacity", inlineStyle);
        var strokeOpacity = Get(attributes, "stroke-opacity", inlineStyle);

        return new SvgStyle
        {
            Fill = Get(attributes, "fill", inlineStyle) ?? Fill,
            Stroke = Get(attributes, "stroke", inlineStyle) ?? Stroke,
            StrokeLineCap = Get(attributes, "stroke-linecap", inlineStyle) ?? StrokeLineCap,
            StrokeLineJoin = Get(attributes, "stroke-linejoin", inlineStyle) ?? StrokeLineJoin,
            FillRule = Get(attributes, "fill-rule", inlineStyle) ?? FillRule,
            StrokeWidth = SvgNumberParser.TryParseNullable(Get(attributes, "stroke-width", inlineStyle)) ?? StrokeWidth,
            Opacity = Opacity * SvgNumberParser.TryParse(opacity, 1D),
            FillOpacity = SvgNumberParser.TryParseNullable(fillOpacity) ?? FillOpacity,
            StrokeOpacity = SvgNumberParser.TryParseNullable(strokeOpacity) ?? StrokeOpacity,
        };
    }

    /// <summary>取属性：先看内联 style，再看直接属性（内联优先）。</summary>
    private static string? Get(
        IReadOnlyDictionary<string, string> attributes,
        string name,
        IReadOnlyDictionary<string, string>? inlineStyle = null)
    {
        // 内联 style 的优先级高于直接属性（CSS 层叠规则：style 属性最具体）。
        // 注意这里与 PCL 原版相反：原版直接属性优先。
        if (inlineStyle is not null
            && inlineStyle.TryGetValue(name, out var styleValue)
            && !string.IsNullOrWhiteSpace(styleValue))
            return styleValue.Trim();

        if (attributes.TryGetValue(name, out var direct) && !string.IsNullOrWhiteSpace(direct))
            return direct.Trim();

        return null;
    }

    /// <summary>解析 <c>style="a:1;b:2"</c> 这种内联样式。</summary>
    private static Dictionary<string, string> ParseStyle(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pair is [{ Length: > 0 }, _])
                result[pair[0]] = pair[1];
        }

        return result;
    }
}

/// <summary>SVG 数值解析。</summary>
public static partial class SvgNumberParser
{
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    /// <summary>解析数值，失败返回 fallback。</summary>
    public static double TryParse(string? value, double fallback = 0D)
        => TryParseNullable(value) ?? fallback;

    /// <summary>解析数值，失败返回 null。</summary>
    public static double? TryParseNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = NumberRegex().Match(value.Trim());
        if (!match.Success)
            return null;

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>解析一串数值。</summary>
    public static double[] ParseNumberList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var matches = NumberRegex().Matches(value);
        var result = new double[matches.Count];
        for (var i = 0; i < matches.Count; i++)
            result[i] = double.Parse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

        return result;
    }
}
