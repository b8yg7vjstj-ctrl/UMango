// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgPathGeometryParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.App.Controls；
//   2. 原版在 Core 里直接产出 WPF Geometry（Core 因此依赖 Windows），
//      UMango 把「路径语义」放在 Core（SvgPathBuilder），这里只做
//      **模型 → WPF 几何**的翻译，于是 Core 保持跨平台可编译；
//   3. 圆弧：Core 模型里存的是 SVG 的「端点 + 半径 + 旋转 + 大弧/顺时针」，
//      这里交给 WPF 的 ArcSegment（其构造函数接受同样的参数，
//      由 WPF 内部做端点→中心换算），与 PCL 原版口径一致。

using System.Windows;
using System.Windows.Media;
using UMango.Core.UI.Svg;

namespace UMango.App.Controls;

/// <summary>把平台中立的路径模型翻译成 WPF 几何。</summary>
public static class SvgGeometryConverter
{
    /// <summary>转换。</summary>
    public static Geometry? ToGeometry(SvgPathGeometry geometry, string? fillRule = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.Figures.Count == 0)
            return null;

        var path = new PathGeometry { FillRule = ParseFillRule(fillRule) };
        foreach (var figure in geometry.Figures)
            path.Figures.Add(ConvertFigure(figure));

        if (path.Figures.Count == 0)
            return null;

        path.Freeze();
        return path;
    }

    private static PathFigure ConvertFigure(SvgPathFigure source)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(source.StartPoint.X, source.StartPoint.Y),
            IsClosed = source.IsClosed,
            IsFilled = source.IsFilled,
        };

        foreach (var segment in source.Segments)
        {
            switch (segment.Kind)
            {
                case SvgSegmentKind.Line:
                    figure.Segments.Add(new LineSegment(
                        new Point(segment.EndPoint.X, segment.EndPoint.Y), true));
                    break;

                case SvgSegmentKind.CubicBezier:
                    figure.Segments.Add(new BezierSegment(
                        new Point(segment.Control1!.Value.X, segment.Control1.Value.Y),
                        new Point(segment.Control2!.Value.X, segment.Control2.Value.Y),
                        new Point(segment.EndPoint.X, segment.EndPoint.Y),
                        true));
                    break;

                case SvgSegmentKind.QuadraticBezier:
                    figure.Segments.Add(new QuadraticBezierSegment(
                        new Point(segment.Control1!.Value.X, segment.Control1.Value.Y),
                        new Point(segment.EndPoint.X, segment.EndPoint.Y),
                        true));
                    break;

                case SvgSegmentKind.Arc:
                    figure.Segments.Add(new ArcSegment(
                        new Point(segment.EndPoint.X, segment.EndPoint.Y),
                        new Size(segment.RadiusX ?? 0D, segment.RadiusY ?? 0D),
                        segment.Rotation ?? 0D,
                        segment.IsLargeArc ?? false,
                        segment.IsClockwise == true ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        true));
                    break;
            }
        }

        return figure;
    }

    private static FillRule ParseFillRule(string? fillRule)
        => fillRule?.Trim().ToLowerInvariant() switch
        {
            "evenodd" => FillRule.EvenOdd,
            _ => FillRule.Nonzero,
        };
}
