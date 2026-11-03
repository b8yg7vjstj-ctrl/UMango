using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UMango.Core.UI.Svg;
using UMango.Shell.Icons;

namespace UMango.Avalonia.Controls;

/// <summary>
/// 运行时渲染 SVG 图标的控件（Avalonia 版，与 WPF 的 <c>App/Controls/SvgIcon.cs</c> 同构）。
///
/// <para>
/// 解析与资源加载都在共享层：<see cref="SvgIconLoader"/> 读 UMango.Shell 的内嵌图标包，
/// Core 的解析器产出平台中立的 <see cref="SvgIconModel"/>。本控件只负责
/// 「把中性路径段转成 Avalonia 的 <see cref="StreamGeometry"/> 并画出来」。
/// </para>
///
/// <para>
/// 与 WPF 版的差异（有意简化，图标集是 lucide 的单色描边图标）：
/// 不支持「使用原始颜色」（<c>UseOriginalColor</c>）——统一用 <see cref="IconBrush"/>；
/// 透明度用 <c>PushOpacity</c> 施加，而不是改画刷副本（Avalonia 的画刷是不可变对象）。
/// </para>
/// </summary>
public class SvgIcon : Control
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<SvgIcon, string>(nameof(Icon), string.Empty);

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<SvgIcon, IBrush?>(nameof(IconBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SvgIcon, double>(nameof(StrokeThickness), 2d);

    private SvgIconModel? _model;
    private string? _loadedIcon;

    static SvgIcon()
    {
        AffectsRender<SvgIcon>(IconProperty, IconBrushProperty, StrokeThicknessProperty);
        AffectsMeasure<SvgIcon>(IconProperty);
    }

    /// <summary>图标标识（「包名/图标名」，如 <c>lucide/home</c>）。</summary>
    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>统一配色（跟随主题 token）。</summary>
    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    /// <summary>未指定 stroke-width 时的描边粗细。</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureModel();
        if (_model is null || _model.Width <= 0 || _model.Height <= 0)
            return default;

        // 没有显式宽高时用 viewBox 的自然尺寸（lucide 是 24×24）；
        // 模板里都会给 16×16，所以实际走的是模板尺寸。
        return new Size(_model.Width, _model.Height);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureModel();

        if (_model is null || _model.Shapes.Count == 0)
            return;

        var brush = IconBrush ?? Brushes.Black;
        var (scale, offsetX, offsetY) = ComputePlacement();

        var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);

        foreach (var shape in _model.Shapes)
        {
            if (shape.Style.Opacity <= 0d) continue;

            var geometry = BuildGeometry(shape.Geometry);
            if (geometry is null) continue;

            using (context.PushTransform(transform))
            {
                var fill = ResolveFill(shape, brush);
                var pen = ResolvePen(shape, brush);

                if (fill is not null)
                {
                    var opacity = Clamp(shape.Style.Opacity * shape.Style.FillOpacity);
                    using (context.PushOpacity(opacity))
                    {
                        context.DrawGeometry(fill, null, geometry);
                    }
                }

                if (pen is not null)
                {
                    var opacity = Clamp(shape.Style.Opacity * shape.Style.StrokeOpacity);
                    using (context.PushOpacity(opacity))
                    {
                        context.DrawGeometry(null, pen, geometry);
                    }
                }
            }
        }
    }

    private void EnsureModel()
    {
        var icon = Icon;
        if (_loadedIcon == icon) return;

        _loadedIcon = icon;
        _model = SvgIconLoader.Load(icon);
    }

    /// <summary>
    /// 「viewBox 坐标 → 控件坐标」的放置参数：先减掉 viewBox 原点，再等比缩放并居中。
    /// 与 WPF 版逐行同口径，避免两个壳画出来的图标大小/位置不一致。
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) ComputePlacement()
    {
        var model = _model!;
        var contentWidth = model.Width;
        var contentHeight = model.Height;
        if (contentWidth <= 0 || contentHeight <= 0)
            return (1d, 0d, 0d);

        var actualWidth = Bounds.Width > 0 ? Bounds.Width : contentWidth;
        var actualHeight = Bounds.Height > 0 ? Bounds.Height : contentHeight;

        var scale = Math.Min(actualWidth / contentWidth, actualHeight / contentHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = 1d;

        var offsetX = (actualWidth - contentWidth * scale) / 2d - model.MinX * scale;
        var offsetY = (actualHeight - contentHeight * scale) / 2d - model.MinY * scale;
        return (scale, offsetX, offsetY);
    }

    /// <summary>把平台中立的路径模型转成 Avalonia 几何。</summary>
    private static StreamGeometry? BuildGeometry(SvgPathGeometry path)
    {
        if (path.Figures.Count == 0)
            return null;

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        foreach (var figure in path.Figures)
        {
            ctx.BeginFigure(ToPoint(figure.StartPoint), figure.IsFilled);

            foreach (var segment in figure.Segments)
            {
                switch (segment.Kind)
                {
                    case SvgSegmentKind.Line:
                        ctx.LineTo(ToPoint(segment.EndPoint));
                        break;

                    case SvgSegmentKind.CubicBezier when segment.Control1 is { } c1 && segment.Control2 is { } c2:
                        ctx.CubicBezierTo(ToPoint(c1), ToPoint(c2), ToPoint(segment.EndPoint));
                        break;

                    case SvgSegmentKind.QuadraticBezier when segment.Control1 is { } c:
                        ctx.QuadraticBezierTo(ToPoint(c), ToPoint(segment.EndPoint));
                        break;

                    case SvgSegmentKind.Arc:
                        ctx.ArcTo(
                            ToPoint(segment.EndPoint),
                            new Size(segment.RadiusX ?? 0d, segment.RadiusY ?? 0d),
                            segment.Rotation ?? 0d,
                            segment.IsLargeArc ?? false,
                            segment.IsClockwise == true
                                ? SweepDirection.Clockwise
                                : SweepDirection.CounterClockwise);
                        break;
                }
            }

            ctx.EndFigure(figure.IsClosed);
        }

        return geometry;
    }

    private static Point ToPoint(SvgPoint point) => new(point.X, point.Y);

    private static IBrush? ResolveFill(SvgShape shape, IBrush brush)
    {
        var style = shape.Style;

        // fill="none" → 只描边（lucide 全部是这种）
        if (IsNone(style.Fill)) return null;

        var hasFill = HasPaint(style.Fill);
        var hasStroke = HasPaint(style.Stroke);

        // 没有 fill 且（有描边 或 这一图元默认就是描边）→ 不填充
        if (!hasFill && (hasStroke || shape.PreferStrokeByDefault)) return null;

        return brush;
    }

    private IPen? ResolvePen(SvgShape shape, IBrush brush)
    {
        var style = shape.Style;

        if (IsNone(style.Stroke)) return null;

        var hasStroke = HasPaint(style.Stroke);
        if (!hasStroke && !shape.PreferStrokeByDefault) return null;

        var thickness = style.StrokeWidth ?? StrokeThickness;
        if (thickness <= 0d) return null;

        return new Pen(
            brush,
            thickness,
            lineCap: ParseLineCap(style.StrokeLineCap),
            lineJoin: ParseLineJoin(style.StrokeLineJoin));
    }

    private static double Clamp(double opacity) => Math.Clamp(opacity, 0d, 1d);

    private static bool HasPaint(string? value) => !string.IsNullOrWhiteSpace(value) && !IsNone(value);

    private static bool IsNone(string? value)
        => string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private static PenLineCap ParseLineCap(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "butt" => PenLineCap.Flat,
        "square" => PenLineCap.Square,
        "round" => PenLineCap.Round,
        _ => PenLineCap.Round,
    };

    private static PenLineJoin ParseLineJoin(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "miter" => PenLineJoin.Miter,
        "bevel" => PenLineJoin.Bevel,
        "round" => PenLineJoin.Round,
        _ => PenLineJoin.Round,
    };
}
