// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIconParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. **剥离 WPF 依赖**：原版产出 WPF Geometry / Brush，UMango 产出平台中立的
//      SvgIconModel（路径段 + 样式），App 层再转 WPF 对象；
//   3. 原版用 XElement 遍历，本文��用平台中立的 SvgElement 树
//      （由 App 层从 XML 读出），Core 因此不依赖 System.Xml.Linq；
//   4. 原版对单个图元解析失败会跳过并继续（不因一个不兼容节点让整个图标不可用），
//      这一点保留。

namespace UMango.Core.UI.Svg;

/// <summary>SVG 图元种类。</summary>
public enum SvgElementKind
{
    Path,
    Line,
    Circle,
    Ellipse,
    Rect,
    Polyline,
    Polygon,
}

/// <summary>SVG 元素（平台中立）。</summary>
public sealed class SvgElement
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Attributes { get; init; }
    public List<SvgElement> Children { get; } = new();
}

/// <summary>解析出的图元。</summary>
public sealed class SvgShape
{
    public required SvgElementKind Kind { get; init; }
    public required SvgPathGeometry Geometry { get; init; }
    public required SvgStyle Style { get; init; }

    /// <summary>线与折线默认用描边而不是填充。</summary>
    public bool PreferStrokeByDefault => Kind is SvgElementKind.Line or SvgElementKind.Polyline;
}

/// <summary>一个 SVG 图标的解析结果。</summary>
public sealed class SvgIconModel
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double Width { get; init; } = 24D;
    public double Height { get; init; } = 24D;
    public IReadOnlyList<SvgShape> Shapes { get; init; } = [];
}

/// <summary>
/// 把 SVG 文档（元素树）解析成图元列表。
/// </summary>
public static class SvgDocumentParser
{
    /// <summary>解析根元素。</summary>
    public static SvgIconModel Parse(SvgElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var (minX, minY, width, height) = ReadViewBox(root);
        var shapes = new List<SvgShape>();

        ReadElements(root, shapes, new SvgStyle());

        return new SvgIconModel
        {
            MinX = minX,
            MinY = minY,
            Width = width <= 0D ? 24D : width,
            Height = height <= 0D ? 24D : height,
            Shapes = shapes,
        };
    }

    private static void ReadElements(SvgElement parent, ICollection<SvgShape> result, SvgStyle inheritedStyle)
    {
        var parentStyle = inheritedStyle.Merge(parent.Attributes);

        foreach (var element in parent.Children)
        {
            var name = element.Name;

            if (name is "g" or "svg")
            {
                ReadElements(element, result, parentStyle);
                continue;
            }

            var style = parentStyle.Merge(element.Attributes);
            var shape = CreateShape(name, element, style);
            if (shape is not null)
                result.Add(shape);
        }
    }

    private static SvgShape? CreateShape(string name, SvgElement element, SvgStyle style)
    {
        try
        {
            return name switch
            {
                "path" => CreatePath(element, style),
                "line" => CreateLine(element, style),
                "circle" => CreateCircle(element, style),
                "ellipse" => CreateEllipse(element, style),
                "rect" => CreateRect(element, style),
                "polyline" => CreatePolyline(element, style),
                "polygon" => CreatePolygon(element, style),
                _ => null,
            };
        }
        catch
        {
            // 单个图元解析失败时跳过，避免一个不兼容节点导致整个图标不可用。
            return null;
        }
    }

    private static SvgShape? CreatePath(SvgElement element, SvgStyle style)
    {
        var d = Attribute(element, "d");
        if (string.IsNullOrWhiteSpace(d))
            return null;

        return new SvgShape
        {
            Kind = SvgElementKind.Path,
            Geometry = SvgPathBuilder.Build(d),
            Style = style,
        };
    }

    private static SvgShape CreateLine(SvgElement element, SvgStyle style)
    {
        var geometry = new SvgPathGeometry();
        var figure = new SvgPathFigure
        {
            StartPoint = new SvgPoint(Number(element, "x1"), Number(element, "y1")),
        };
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = new SvgPoint(Number(element, "x2"), Number(element, "y2")),
        });
        geometry.Figures.Add(figure);

        return new SvgShape
        {
            Kind = SvgElementKind.Line,
            Geometry = geometry,
            Style = style,
        };
    }

    private static SvgShape CreateCircle(SvgElement element, SvgStyle style)
    {
        var cx = Number(element, "cx");
        var cy = Number(element, "cy");
        var r = Number(element, "r");
        return new SvgShape
        {
            Kind = SvgElementKind.Circle,
            Geometry = Ellipse(cx, cy, r, r),
            Style = style,
        };
    }

    private static SvgShape CreateEllipse(SvgElement element, SvgStyle style)
    {
        var cx = Number(element, "cx");
        var cy = Number(element, "cy");
        return new SvgShape
        {
            Kind = SvgElementKind.Ellipse,
            Geometry = Ellipse(cx, cy, Number(element, "rx"), Number(element, "ry")),
            Style = style,
        };
    }

    private static SvgShape CreateRect(SvgElement element, SvgStyle style)
    {
        var x = Number(element, "x");
        var y = Number(element, "y");
        var width = Number(element, "width");
        var height = Number(element, "height");
        var rx = Number(element, "rx", Number(element, "ry"));
        var ry = Number(element, "ry", rx);

        var geometry = new SvgPathGeometry();
        var figure = new SvgPathFigure { StartPoint = new SvgPoint(x, y) };
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = new SvgPoint(x + width, y),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = new SvgPoint(x + width, y + height),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = new SvgPoint(x, y + height),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.Line,
            EndPoint = new SvgPoint(x, y),
        });
        figure.IsClosed = true;
        geometry.Figures.Add(figure);

        _ = rx;
        _ = ry;
        return new SvgShape
        {
            Kind = SvgElementKind.Rect,
            Geometry = geometry,
            Style = style,
        };
    }

    private static SvgShape? CreatePolyline(SvgElement element, SvgStyle style)
    {
        var geometry = Points(Attribute(element, "points"), close: false);
        if (geometry is null)
            return null;

        return new SvgShape
        {
            Kind = SvgElementKind.Polyline,
            Geometry = geometry,
            Style = style,
        };
    }

    private static SvgShape? CreatePolygon(SvgElement element, SvgStyle style)
    {
        var geometry = Points(Attribute(element, "points"), close: true);
        if (geometry is null)
            return null;

        return new SvgShape
        {
            Kind = SvgElementKind.Polygon,
            Geometry = geometry,
            Style = style,
        };
    }

    private static SvgPathGeometry Points(string? points, bool close)
    {
        var numbers = SvgNumberParser.ParseNumberList(points);
        var geometry = new SvgPathGeometry();
        if (numbers.Length < 4)
            return geometry;

        var figure = new SvgPathFigure
        {
            StartPoint = new SvgPoint(numbers[0], numbers[1]),
            IsClosed = close,
        };
        for (var i = 2; i + 1 < numbers.Length; i += 2)
            figure.Segments.Add(new SvgPathSegment
            {
                Kind = SvgSegmentKind.Line,
                EndPoint = new SvgPoint(numbers[i], numbers[i + 1]),
            });
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static SvgPathGeometry Ellipse(double cx, double cy, double rx, double ry)
    {
        // 椭圆用四段三次贝塞尔近似（kappa = 0.5522847498307936）。
        const double kappa = 0.5522847498307936;
        var geometry = new SvgPathGeometry();
        var figure = new SvgPathFigure { StartPoint = new SvgPoint(cx + rx, cy) };
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.CubicBezier,
            Control1 = new SvgPoint(cx + rx, cy + ry * kappa),
            Control2 = new SvgPoint(cx + rx * kappa, cy + ry),
            EndPoint = new SvgPoint(cx, cy + ry),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.CubicBezier,
            Control1 = new SvgPoint(cx - rx * kappa, cy + ry),
            Control2 = new SvgPoint(cx - rx, cy + ry * kappa),
            EndPoint = new SvgPoint(cx - rx, cy),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.CubicBezier,
            Control1 = new SvgPoint(cx - rx, cy - ry * kappa),
            Control2 = new SvgPoint(cx - rx * kappa, cy - ry),
            EndPoint = new SvgPoint(cx, cy - ry),
        });
        figure.Segments.Add(new SvgPathSegment
        {
            Kind = SvgSegmentKind.CubicBezier,
            Control1 = new SvgPoint(cx + rx * kappa, cy - ry),
            Control2 = new SvgPoint(cx + rx, cy - ry * kappa),
            EndPoint = new SvgPoint(cx + rx, cy),
        });
        figure.IsClosed = true;
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static (double MinX, double MinY, double Width, double Height) ReadViewBox(SvgElement root)
    {
        var viewBox = Attribute(root, "viewBox");
        var numbers = SvgNumberParser.ParseNumberList(viewBox);
        if (numbers.Length == 4)
            return (numbers[0], numbers[1], numbers[2], numbers[3]);

        var width = SvgNumberParser.TryParseNullable(Attribute(root, "width")) ?? 24D;
        var height = SvgNumberParser.TryParseNullable(Attribute(root, "height")) ?? 24D;
        return (0D, 0D, width, height);
    }

    private static double Number(SvgElement element, string name, double fallback = 0D)
        => SvgNumberParser.TryParse(Attribute(element, name), fallback);

    private static string? Attribute(SvgElement element, string name)
        => element.Attributes.TryGetValue(name, out var value) ? value : null;
}
