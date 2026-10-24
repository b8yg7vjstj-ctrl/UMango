// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgPathGeometryParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. **剥离 WPF 依赖**：原版直接产出 System.Windows.Media.Geometry，
//      UMango 改为产出平台中立的路径段模型，App 层再转成 WPF Geometry。
//      好处是路径解析能在 net10.0 支编译、在 macOS 上被单元测试覆盖；
//   3. 圆弧参数原版直接交给 WPF 的 ArcSegment（由 WPF 内部做端点→中心换算），
//      UMango 保持同样口径：中性模型里存 rx/ry/旋转角/大弧/顺时针/终点，
//      换算交给适配层。

namespace UMango.Core.UI.Svg;

/// <summary>一个二维点。</summary>
public readonly record struct SvgPoint(double X, double Y);

/// <summary>路径段类型。</summary>
public enum SvgSegmentKind
{
    Line,
    CubicBezier,
    QuadraticBezier,
    Arc,
}

/// <summary>一个路径段。</summary>
public sealed class SvgPathSegment
{
    public SvgSegmentKind Kind { get; init; }
    public SvgPoint EndPoint { get; init; }

    /// <summary>三次贝塞尔的两个控制点（<see cref="SvgSegmentKind.CubicBezier"/>）。</summary>
    public SvgPoint? Control1 { get; init; }

    /// <summary>三次/二次贝塞尔的第二个控制点（仅三次贝塞尔使用）。</summary>
    public SvgPoint? Control2 { get; init; }

    /// <summary>圆弧的横纵半径。</summary>
    public double? RadiusX { get; init; }

    /// <summary>圆弧的纵半径。</summary>
    public double? RadiusY { get; init; }

    /// <summary>圆弧的 x 轴旋转角（度）。</summary>
    public double? Rotation { get; init; }

    /// <summary>是否取大弧。</summary>
    public bool? IsLargeArc { get; init; }

    /// <summary>是否顺时针。</summary>
    public bool? IsClockwise { get; init; }
}

/// <summary>一个子路径。</summary>
public sealed class SvgPathFigure
{
    public SvgPoint StartPoint { get; init; }
    public bool IsClosed { get; set; }
    public bool IsFilled { get; init; } = true;
    public List<SvgPathSegment> Segments { get; } = new();
}

/// <summary>一条解析好的 SVG 路径。</summary>
public sealed class SvgPathGeometry
{
    public List<SvgPathFigure> Figures { get; } = new();
}
