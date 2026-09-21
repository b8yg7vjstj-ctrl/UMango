using System.Windows;
using UMango.Core.Animation;

namespace UMango.App.Animation;

/// <summary>
/// 把 WPF 元素的属性包装成 Core 的 IAnimatable。
///
/// Core 动画引擎只关心"读一个值、写一个值"（IAnimatable），
/// 不知道背后是 WPF 的 TranslateTransform、Opacity 还是别的什么。
/// 这个适配器把 WPF 的具体属性接进来。
/// </summary>
public sealed class WpfAnimatable : IAnimatable
{
    private readonly Func<double> _getter;
    private readonly Action<double> _setter;

    public WpfAnimatable(Func<double> getter, Action<double> setter)
    {
        _getter = getter;
        _setter = setter;
    }

    public object? GetValue() => _getter();

    public void SetValue(object? value)
    {
        if (value is double d) _setter(d);
        else if (value is int i) _setter(i);
        else if (value is float f) _setter(f);
    }

    /// <summary>为 FrameworkElement 创建一组动画目标：X/Y 位移 + 透明度。</summary>
    public static (WpfAnimatable X, WpfAnimatable Y, WpfAnimatable Opacity) FromElement(FrameworkElement element)
    {
        var transform = element.RenderTransform as System.Windows.Media.TranslateTransform
                       ?? new System.Windows.Media.TranslateTransform();

        // 设置 TransformOrigin 为中心（动画从中心点展开）
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is not System.Windows.Media.TranslateTransform)
            element.RenderTransform = transform;

        var x = new WpfAnimatable(() => transform.X, v => transform.X = v);
        var y = new WpfAnimatable(() => transform.Y, v => transform.Y = v);
        var o = new WpfAnimatable(() => element.Opacity, v => element.Opacity = v);

        return (x, y, o);
    }

    /// <summary>重置元素到默认状态（清除变换和透明度）。</summary>
    public static void Reset(FrameworkElement element)
    {
        element.RenderTransform = new System.Windows.Media.TranslateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.Opacity = 1.0;
    }
}
