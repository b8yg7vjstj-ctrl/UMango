using UMango.Core.UI.Svg;
using Xunit;

namespace UMango.Core.Tests.UI.Svg;

/// <summary>
/// SVG 路径解析。
///
/// 期望值按 SVG 1.1 规范写，不是照抄 PCL 原版。
/// </summary>
public class SvgPathTests
{
    [Fact]
    public void 解析移动与直线()
    {
        var geometry = SvgPathBuilder.Build("M10 20 L30 40");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new SvgPoint(10, 20), figure.StartPoint);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.Line, segment.Kind);
        Assert.Equal(new SvgPoint(30, 40), segment.EndPoint);
    }

    [Fact]
    public void 相对移动基于原点()
    {
        var geometry = SvgPathBuilder.Build("m10 20");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new SvgPoint(10, 20), figure.StartPoint);
    }

    [Fact]
    public void 相对移动基于当前点而不是原点()
    {
        // "M10 10 m5 5"：第二个 m 是相对移动，起点应是 (15,15) 而不是 (5,5)。
        // 这条用例盯住的是「相对」的基准 —— 注入「相对按绝对处理」时它必须变红。
        var geometry = SvgPathBuilder.Build("M10 10 m5 5");
        Assert.Equal(2, geometry.Figures.Count);
        Assert.Equal(new SvgPoint(15, 15), geometry.Figures[1].StartPoint);
    }

    [Fact]
    public void 相对线段基于当前点()
    {
        var geometry = SvgPathBuilder.Build("M10 10 l5 5");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(new SvgPoint(15, 15), segment.EndPoint);
    }

    [Fact]
    public void 移动之后的坐标对按直线处理()
    {
        var geometry = SvgPathBuilder.Build("M10 10 20 20 30 30");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(2, figure.Segments.Count);
        Assert.Equal(new SvgPoint(20, 20), figure.Segments[0].EndPoint);
        Assert.Equal(new SvgPoint(30, 30), figure.Segments[1].EndPoint);
    }

    [Fact]
    public void 水平与垂直线()
    {
        var geometry = SvgPathBuilder.Build("M0 0 H10 V20 h-5 v-5");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(4, figure.Segments.Count);
        Assert.Equal(new SvgPoint(10, 0), figure.Segments[0].EndPoint);
        Assert.Equal(new SvgPoint(10, 20), figure.Segments[1].EndPoint);
        Assert.Equal(new SvgPoint(5, 20), figure.Segments[2].EndPoint);
        Assert.Equal(new SvgPoint(5, 15), figure.Segments[3].EndPoint);
    }

    [Fact]
    public void 三次贝塞尔保留两个控制点()
    {
        var geometry = SvgPathBuilder.Build("M0 0 C10 20 30 40 50 60");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.CubicBezier, segment.Kind);
        Assert.Equal(new SvgPoint(10, 20), segment.Control1);
        Assert.Equal(new SvgPoint(30, 40), segment.Control2);
        Assert.Equal(new SvgPoint(50, 60), segment.EndPoint);
    }

    [Fact]
    public void 光滑三次贝塞尔镜像上一次的第二个控制点()
    {
        // C 之后的 S：第一个控制点应是 C 的第二个控制点 (30,40) 关于当前点 (50,60) 的镜像。
        var geometry = SvgPathBuilder.Build("M0 0 C10 20 30 40 50 60 S70 80 90 100");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(2, figure.Segments.Count);
        var smooth = figure.Segments[1];
        Assert.Equal(SvgSegmentKind.CubicBezier, smooth.Kind);
        Assert.Equal(new SvgPoint(70, 80), smooth.Control1);
        Assert.Equal(new SvgPoint(90, 100), smooth.EndPoint);
    }

    [Fact]
    public void 首次光滑三次贝塞尔的第一个控制点就是当前点()
    {
        var geometry = SvgPathBuilder.Build("M0 0 S30 40 50 60");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(new SvgPoint(0, 0), segment.Control1);
    }

    [Fact]
    public void 二次贝塞尔()
    {
        var geometry = SvgPathBuilder.Build("M0 0 Q10 20 30 40");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.QuadraticBezier, segment.Kind);
        Assert.Equal(new SvgPoint(10, 20), segment.Control1);
        Assert.Equal(new SvgPoint(30, 40), segment.EndPoint);
    }

    [Fact]
    public void 光滑二次贝塞尔镜像上一次的控点()
    {
        var geometry = SvgPathBuilder.Build("M0 0 Q10 20 30 40 T60 80");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(2, figure.Segments.Count);
        var smooth = figure.Segments[1];
        // Q 的控制点 (10,20) 关于当前点 (30,40) 的镜像 = (50,60)
        Assert.Equal(new SvgPoint(50, 60), smooth.Control1);
        Assert.Equal(new SvgPoint(60, 80), smooth.EndPoint);
    }

    [Fact]
    public void 圆弧保留参数()
    {
        var geometry = SvgPathBuilder.Build("M0 0 A10 20 30 1 0 50 60");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.Arc, segment.Kind);
        Assert.Equal(10, segment.RadiusX);
        Assert.Equal(20, segment.RadiusY);
        Assert.Equal(30, segment.Rotation);
        Assert.True(segment.IsLargeArc);
        Assert.False(segment.IsClockwise);
        Assert.Equal(new SvgPoint(50, 60), segment.EndPoint);
    }

    [Fact]
    public void 半径为零的圆弧退化成直线()
    {
        var geometry = SvgPathBuilder.Build("M0 0 A0 0 0 0 0 50 60");
        var figure = Assert.Single(geometry.Figures);
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.Line, segment.Kind);
    }

    [Fact]
    public void 闭合子路径把当前点移回起点()
    {
        var geometry = SvgPathBuilder.Build("M10 10 L20 20 Z L30 30");
        Assert.Equal(2, geometry.Figures.Count);
        var first = geometry.Figures[0];
        Assert.True(first.IsClosed);
        // Z 之后若没有新的 M，后续绘制指令会**另起一个子路径**（SVG 规范）。
        Assert.Equal(2, geometry.Figures.Count);
        var second = geometry.Figures[1];
        Assert.Equal(new SvgPoint(10, 10), second.StartPoint);
        Assert.Equal(new SvgPoint(30, 30), Assert.Single(second.Segments).EndPoint);
    }

    [Fact]
    public void 多个子路径()
    {
        var geometry = SvgPathBuilder.Build("M0 0 L1 1 M10 10 L11 11");
        Assert.Equal(2, geometry.Figures.Count);
        Assert.Equal(new SvgPoint(10, 10), geometry.Figures[1].StartPoint);
    }

    [Fact]
    public void 数字支持小数与科学计数法()
    {
        var geometry = SvgPathBuilder.Build("M1.5 2.25 L1e1 2e1");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new SvgPoint(1.5, 2.25), figure.StartPoint);
        Assert.Equal(new SvgPoint(10, 20), figure.Segments[0].EndPoint);
    }

    [Fact]
    public void 逗号可以作分隔符()
    {
        var geometry = SvgPathBuilder.Build("M1,2 L3,4");
        var figure = Assert.Single(geometry.Figures);
        Assert.Equal(new SvgPoint(1, 2), figure.StartPoint);
    }

    [Fact]
    public void 没有命令开头会报错()
    {
        Assert.Throws<FormatException>(() => SvgPathBuilder.Build("10 20"));
    }

    [Fact]
    public void 非法数字会报错()
    {
        Assert.Throws<FormatException>(() => SvgPathBuilder.Build("M10 x"));
    }
}
