// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIcon.cs 与 SvgIconLoader.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Controls.SvgIcon → UMango.App.Controls；
//   2. **加载器已上移到 UMango.Shell.Icons.SvgIconLoader**（图标包是两个壳共用的
//      一份内嵌资源）；本文件只剩「把中性路径模型画出来」的 WPF 部分；
//   3. 原版的 SvgIconModel 在 Core 内是 internal，这里改为公开的
//      UMango.Core.UI.Svg.SvgIconModel，App 层直接复用；
//   4. 原版固定用 SystemColors.ControlTextBrush 作默认笔刷，
//      UMango 保留该默认，但允许外部指定（跟随主题色）。

using System.Windows;
using System.Windows.Media;
using UMango.Core.UI.Svg;
using UMango.Shell.Icons;

namespace UMango.App.Controls;

/// <summary>
/// 运行时渲染 SVG 图标的控件。
///
/// 用法：<c>&lt;controls:SvgIcon Icon="pack/name" /&gt;</c>
/// </summary>
public class SvgIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(SvgIcon),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            _OnIconChanged));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush),
        typeof(Brush),
        typeof(SvgIcon),
        new FrameworkPropertyMetadata(
            SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(SvgIcon),
        new FrameworkPropertyMetadata(
            2D,
            FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double number && !double.IsNaN(number) && number >= 0D);

    public static readonly DependencyProperty UseOriginalColorProperty = DependencyProperty.Register(
        nameof(UseOriginalColor),
        typeof(bool),
        typeof(SvgIcon),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch),
        typeof(Stretch),
        typeof(SvgIcon),
        new FrameworkPropertyMetadata(
            Stretch.Uniform,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private SvgIconModel? _model;
    private bool _modelLoaded;

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush IconBrush
    {
        get => (Brush)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool UseOriginalColor
    {
        get => (bool)GetValue(UseOriginalColorProperty);
        set => SetValue(UseOriginalColorProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>已解析出的图元（供测试与高级用法检查）。</summary>
    public IReadOnlyList<SvgIconElement> Elements { get; private set; } = [];

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        EnsureModel();
        if (Elements.Count == 0)
            return;

        var options = new SvgPaintOptions(
            IconBrush ?? SystemColors.ControlTextBrush,
            StrokeThickness,
            UseOriginalColor);

        var (scale, offsetX, offsetY) = ComputePlacement();

        foreach (var element in Elements)
        {
            var geometry = element.BuildGeometry();
            if (geometry is null)
                continue;

            // 一次把「缩放 + 平移」合成单个仿射矩阵：
            //   x' = scale·x + offsetX
            //   y' = scale·y + offsetY
            // 用一个矩阵而不是叠加多个 PushTransform，既避免依赖推入次序，
            // 也保证每个图元只被变换一次。
            drawingContext.PushTransform(new MatrixTransform(scale, 0D, 0D, scale, offsetX, offsetY));
            element.Draw(drawingContext, options);
            drawingContext.Pop();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureModel();
        if (_model is null)
            return new Size(0, 0);

        var model = _model;
        var width = model.Width;
        var height = model.Height;
        if (width <= 0 || height <= 0)
            return new Size(0, 0);

        var scale = 1D;
        if (availableSize.Width is > 0 && availableSize.Height > 0)
        {
            scale = Math.Min(availableSize.Width / width, availableSize.Height / height);
        }

        return new Size(width * scale, height * scale);
    }

    /// <summary>
    /// 算出「viewBox 坐标 → 控件像素坐标」的放置参数。
    ///
    /// 两个要点：
    ///   1. **要缩放**：SVG 几何是 viewBox 坐标系（lucide 是 24×24），
    ///      而控件可能只有 16×16 —— 不缩放就会画到框外面去；
    ///   2. **要减掉 viewBox 的原点偏移**（minX/minY）：viewBox 不一定从 (0,0) 起。
    /// </summary>
    private (double Scale, double OffsetX, double OffsetY) ComputePlacement()
    {
        var model = _model!;
        var contentWidth = model.Width;
        var contentHeight = model.Height;
        if (contentWidth <= 0 || contentHeight <= 0)
            return (1D, 0D, 0D);

        var actualWidth = ActualWidth > 0 ? ActualWidth : contentWidth;
        var actualHeight = ActualHeight > 0 ? ActualHeight : contentHeight;

        var scale = Math.Min(actualWidth / contentWidth, actualHeight / contentHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = 1D;

        // 先把 viewBox 原点挪到 (0,0)，再等比缩放，最后在控件里居中。
        var offsetX = (actualWidth - contentWidth * scale) / 2D - model.MinX * scale;
        var offsetY = (actualHeight - contentHeight * scale) / 2D - model.MinY * scale;
        return (scale, offsetX, offsetY);
    }

    private void EnsureModel()
    {
        if (_modelLoaded)
            return;

        _modelLoaded = true;
        _model = SvgIconLoader.Load(Icon);
        Elements = _model is null
            ? []
            : _model.Shapes.Select(shape => new SvgIconElement
            {
                Kind = shape.Kind,
                Geometry = shape.Geometry,
                Style = shape.Style,
            }).ToList();
    }

    private static void _OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (SvgIcon)d;
        icon._modelLoaded = false;
        icon._model = null;
        icon.Elements = [];
        icon.InvalidateMeasure();
        icon.InvalidateVisual();
    }
}

// 图标加载（读内嵌资源 + 解析 + 缓存）已上移到 UMango.Shell.Icons.SvgIconLoader：
// 图标包是 WPF 与 Avalonia 共用的同一份资源，加载逻辑只应有一份。
// 本文件只保留「把中性路径模型画成 WPF Geometry」这一层。

