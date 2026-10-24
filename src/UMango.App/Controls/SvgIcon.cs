// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgIcon.cs 与 SvgIconLoader.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Controls.SvgIcon → UMango.App.Controls；
//   2. 原版把「加载 pack URI 里的 svg」与「画」写在一起，
//      UMango 拆成 SvgIconLoader（加载/缓存）与 SvgIcon（控件）两个文件，
//      加载器支持从任意流读入（便于测试与自定义图标包）；
//   3. 原版的 SvgIconModel 在 Core 内是 internal，这里改为公开的
//      UMango.Core.UI.Svg.SvgIconModel，App 层直接复用；
//   4. 原版固定用 SystemColors.ControlTextBrush 作默认笔刷，
//      UMango 保留该默认，但允许外部指定（跟随主题色）。

using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using UMango.Core.UI.Svg;

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

        var model = _model!;
        var bounds = ComputeBounds();

        var options = new SvgPaintOptions(
            IconBrush ?? SystemColors.ControlTextBrush,
            StrokeThickness,
            UseOriginalColor);

        foreach (var element in Elements)
        {
            var geometry = element.BuildGeometry();
            if (geometry is null)
                continue;

            drawingContext.PushTransform(new TranslateTransform(bounds.Left, bounds.Top));
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
        var width = model.Width - model.MinX;
        var height = model.Height - model.MinY;
        if (width <= 0 || height <= 0)
            return new Size(0, 0);

        var scale = 1D;
        if (availableSize.Width is > 0 && availableSize.Height > 0)
        {
            scale = Math.Min(availableSize.Width / width, availableSize.Height / height);
        }

        return new Size(width * scale, height * scale);
    }

    private Rect ComputeBounds()
    {
        var model = _model!;
        var width = model.Width - model.MinX;
        var height = model.Height - model.MinY;
        var actual = ActualWidth > 0 ? ActualWidth : width;
        var actualHeight = ActualHeight > 0 ? ActualHeight : height;
        var scale = width > 0 ? actual / width : 1D;
        var scaleY = height > 0 ? actualHeight / height : 1D;
        var scaleValue = Math.Min(scale, scaleY);
        return new Rect(
            (actual - width * scaleValue) / 2D,
            (actualHeight - height * scaleValue) / 2D,
            width * scaleValue,
            height * scaleValue);
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

/// <summary>SVG 图标加载器（含缓存）。</summary>
public static class SvgIconLoader
{
    /// <summary>默认图标包名。</summary>
    public const string DefaultIconPack = SvgIconKey.DefaultIconPack;

    private static readonly ConcurrentDictionary<string, Lazy<SvgIconModel?>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按标识加载图标（从程序集的图标包资源读）。</summary>
    public static SvgIconModel? Load(string? icon, string? defaultPack = null)
    {
        var key = SvgIconKey.TryParse(icon, defaultPack ?? DefaultIconPack);
        if (key is null)
            return null;

        var cacheKey = key.Value.ToString();
        return Cache.GetOrAdd(cacheKey, _ => new Lazy<SvgIconModel?>(() => LoadCore(key.Value))).Value;
    }

    /// <summary>从 SVG 源码解析图标（不走资源、不入缓存）。</summary>
    public static SvgIconModel? ParseSource(string svg)
    {
        try
        {
            var root = SvgSourceParser.ParseSource(svg);
            return SvgDocumentParser.Parse(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空缓存。</summary>
    public static void ClearCache() => Cache.Clear();

    private static SvgIconModel? LoadCore(SvgIconKey key)
    {
        try
        {
            var assemblyName = typeof(SvgIconLoader).Assembly.GetName().Name ?? "UMango";
            var uri = new Uri(
                $"pack://application:,,,/{assemblyName};component/Assets/IconPacks/{key.Pack}/{key.Name}.svg",
                UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is null)
                return null;

            using var stream = info.Stream;
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return ParseSource(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }
}
