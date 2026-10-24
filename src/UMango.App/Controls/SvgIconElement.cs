// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIconElement.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Controls.SvgIcon → UMango.App.Controls；
//   2. 原版的 SvgIconElement 直接持有 WPF Geometry，UMango 改为持有平台中立的
//      SvgPathGeometry，由本文件提供 SvgGeometryConverter 转成 WPF Geometry；
//   3. 原版的绘制逻辑（填充/描边解析、透明度叠加、线帽线接头）完整保留。

using System.Windows.Media;
using UMango.Core.UI.Svg;

namespace UMango.App.Controls;

/// <summary>绘制一个 SVG 图元所需的选项。</summary>
public readonly record struct SvgPaintOptions(
    Brush IconBrush,
    double StrokeThickness,
    bool UseOriginalColor);

/// <summary>一个可绘制的 SVG 图元。</summary>
public sealed class SvgIconElement
{
    public required SvgElementKind Kind { get; init; }
    public required SvgPathGeometry Geometry { get; init; }
    public required SvgStyle Style { get; init; }

    /// <summary>线与折线默认用描边而不是填充。</summary>
    public bool PreferStrokeByDefault => Kind is SvgElementKind.Line or SvgElementKind.Polyline;

    /// <summary>把中性路径转成 WPF 几何。</summary>
    public Geometry? BuildGeometry() => SvgGeometryConverter.ToGeometry(Geometry, Style.FillRule);

    public void Draw(DrawingContext context, SvgPaintOptions options)
    {
        if (Style.Opacity <= 0D)
            return;

        var geometry = BuildGeometry();
        if (geometry is null)
            return;

        var fill = ResolveFill(options);
        var pen = ResolvePen(options);

        if (fill is null && pen is null)
            return;

        context.DrawGeometry(fill, pen, geometry);
    }

    private Brush? ResolveFill(SvgPaintOptions options)
    {
        var hasFill = HasPaint(Style.Fill);
        var hasStroke = HasPaint(Style.Stroke);
        var explicitlyNoFill = IsNone(Style.Fill);
        Brush? brush;

        if (!options.UseOriginalColor)
        {
            if (explicitlyNoFill)
                return null;

            if (!hasFill && (hasStroke || PreferStrokeByDefault))
                return null;

            brush = options.IconBrush;
        }
        else
        {
            if (explicitlyNoFill)
                return null;

            if (hasFill)
                brush = SvgPaintParser.ParseBrush(Style.Fill, options.IconBrush);
            else if (!hasStroke && !PreferStrokeByDefault)
                brush = Brushes.Black;
            else
                return null;
        }

        return ApplyOpacity(brush, Style.Opacity * Style.FillOpacity);
    }

    private Pen? ResolvePen(SvgPaintOptions options)
    {
        var hasStroke = HasPaint(Style.Stroke);
        var explicitlyNoStroke = IsNone(Style.Stroke);
        Brush? brush;

        if (!options.UseOriginalColor)
        {
            if (explicitlyNoStroke)
                return null;

            if (!hasStroke && !PreferStrokeByDefault)
                return null;

            brush = options.IconBrush;
        }
        else
        {
            if (explicitlyNoStroke)
                return null;

            if (hasStroke)
                brush = SvgPaintParser.ParseBrush(Style.Stroke, options.IconBrush);
            else if (PreferStrokeByDefault)
                brush = Brushes.Black;
            else
                return null;
        }

        return CreatePen(ApplyOpacity(brush, Style.Opacity * Style.StrokeOpacity),
            Style.StrokeWidth ?? options.StrokeThickness);
    }

    private Pen? CreatePen(Brush? brush, double thickness)
    {
        if (brush is null || thickness <= 0D)
            return null;

        return new Pen(brush, thickness)
        {
            StartLineCap = ParseLineCap(Style.StrokeLineCap),
            EndLineCap = ParseLineCap(Style.StrokeLineCap),
            LineJoin = ParseLineJoin(Style.StrokeLineJoin),
        };
    }

    private static Brush? ApplyOpacity(Brush? brush, double opacity)
    {
        if (brush is null)
            return null;

        opacity = Math.Clamp(opacity, 0D, 1D);
        if (opacity <= 0D)
            return null;

        if (Math.Abs(opacity - 1D) < 0.0001D)
            return brush;

        var clone = brush.CloneCurrentValue();
        clone.Opacity *= opacity;
        if (clone.CanFreeze)
            clone.Freeze();
        return clone;
    }

    private static bool HasPaint(string? value)
        => !string.IsNullOrWhiteSpace(value) && !IsNone(value);

    private static bool IsNone(string? value)
        => string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private static PenLineCap ParseLineCap(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "butt" => PenLineCap.Flat,
            "square" => PenLineCap.Square,
            "round" => PenLineCap.Round,
            _ => PenLineCap.Round,
        };

    private static PenLineJoin ParseLineJoin(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "miter" => PenLineJoin.Miter,
            "bevel" => PenLineJoin.Bevel,
            "round" => PenLineJoin.Round,
            _ => PenLineJoin.Round,
        };
}
