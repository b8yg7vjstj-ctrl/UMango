using UMango.Core.UI.Svg;
using Xunit;

namespace UMango.Core.Tests.UI.Svg;

/// <summary>SVG 文档解析。</summary>
public class SvgDocumentTests
{
    private const string SimpleIcon = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
          <path d="M4 4 L20 20" stroke="#ff0000" stroke-width="2" fill="none" />
        </svg>
        """;

    [Fact]
    public void 解析出路径图元()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource(SimpleIcon));
        var shape = Assert.Single(model.Shapes);
        Assert.Equal(SvgElementKind.Path, shape.Kind);
        var figure = Assert.Single(shape.Geometry.Figures);
        // "M4 4 L20 20"：M 只移动不画线，L 才是唯一的线段。
        var segment = Assert.Single(figure.Segments);
        Assert.Equal(SvgSegmentKind.Line, segment.Kind);
        Assert.Equal(new SvgPoint(20, 20), segment.EndPoint);
    }

    [Fact]
    public void 读出视口尺寸()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource(SimpleIcon));
        Assert.Equal(0, model.MinX);
        Assert.Equal(0, model.MinY);
        Assert.Equal(24, model.Width);
        Assert.Equal(24, model.Height);
    }

    [Fact]
    public void 没有视口时回退到宽高属性()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource(
            "<svg width=\"32\" height=\"16\"><path d=\"M0 0 L1 1\" /></svg>"));
        Assert.Equal(32, model.Width);
        Assert.Equal(16, model.Height);
    }

    [Fact]
    public void 读出描边颜色()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource(SimpleIcon));
        Assert.Equal("#ff0000", model.Shapes[0].Style.Stroke);
        Assert.Equal("none", model.Shapes[0].Style.Fill);
        Assert.Equal(2, model.Shapes[0].Style.StrokeWidth);
    }

    [Fact]
    public void 组元素里的样式会被子元素继承()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource("""
            <svg viewBox="0 0 24 24">
              <g fill="#00ff00" stroke-width="3">
                <path d="M0 0 L1 1" />
              </g>
            </svg>
            """));
        var shape = Assert.Single(model.Shapes);
        Assert.Equal("#00ff00", shape.Style.Fill);
        Assert.Equal(3, shape.Style.StrokeWidth);
    }

    [Fact]
    public void 内联样式优先于直接属性()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource("""
            <svg viewBox="0 0 24 24">
              <path d="M0 0 L1 1" fill="#111111" style="fill:#222222" />
            </svg>
            """));
        Assert.Equal("#222222", model.Shapes[0].Style.Fill);
    }

    [Fact]
    public void 解析圆与椭圆()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource("""
            <svg viewBox="0 0 24 24">
              <circle cx="12" cy="12" r="6" />
              <ellipse cx="4" cy="4" rx="2" ry="3" />
            </svg>
            """));
        Assert.Equal(2, model.Shapes.Count);
        Assert.Equal(SvgElementKind.Circle, model.Shapes[0].Kind);
        Assert.Equal(SvgElementKind.Ellipse, model.Shapes[1].Kind);
    }

    [Fact]
    public void 解析矩形与折线()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource("""
            <svg viewBox="0 0 24 24">
              <rect x="1" y="2" width="10" height="20" />
              <polyline points="0,0 5,5 10,0" />
              <polygon points="0,0 5,5 10,0" />
            </svg>
            """));
        Assert.Equal(3, model.Shapes.Count);
        Assert.Equal(SvgElementKind.Rect, model.Shapes[0].Kind);
        Assert.Equal(SvgElementKind.Polyline, model.Shapes[1].Kind);
        Assert.Equal(SvgElementKind.Polygon, model.Shapes[2].Kind);
    }

    [Fact]
    public void 单个图元解析失败不影响其它图元()
    {
        var model = SvgDocumentParser.Parse(SvgSourceParser.ParseSource("""
            <svg viewBox="0 0 24 24">
              <path d="这不是合法路径" />
              <path d="M0 0 L1 1" />
            </svg>
            """));
        var shape = Assert.Single(model.Shapes);
        Assert.Equal(new SvgPoint(0, 0), shape.Geometry.Figures[0].StartPoint);
    }

    [Fact]
    public void 拒绝外部实体()
    {
        // XXE 防护：带 DTD 的输入必须被拒绝。
        Assert.ThrowsAny<System.Xml.XmlException>(() =>
            SvgSourceParser.ParseSource("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg/>"));
    }

    [Fact]
    public void 命名空间声明不出现在属性里()
    {
        var root = SvgSourceParser.ParseSource(SimpleIcon);
        Assert.DoesNotContain(root.Attributes.Keys, k => k.StartsWith("xmlns", StringComparison.Ordinal));
    }
}
