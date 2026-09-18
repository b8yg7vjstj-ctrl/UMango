using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UMango.Core.Animation;

namespace UMango.App.Animation;

/// <summary>
/// 把 Core 算好的 <see cref="AnimationSpec"/> 播放到 WPF 元素上。
///
/// 本类刻意**很薄**：所有数值（位移方向、距离、时长、缓动种类）都由
/// <see cref="AnimationPlanner"/> 决定，这里只做"翻译成 WPF 的动画对象"。
/// 原因是 WPF 代码在 macOS 上既跑不了也测不了（见 DownloaderSession 的注释），
/// 所以能算的都算在 Core 里。
/// </summary>
public static class AnimationPlayer
{
    /// <summary>播放入场动画（位移 + 淡入）。</summary>
    public static void PlayEntrance(FrameworkElement element, ElementMotion motion, AnimationSettings? settings = null)
    {
        var spec = AnimationPlanner.Plan(motion, settings ?? Services.AppSettings.Animation);

        if (spec.IsInstant)
        {
            Reset(element);
            return;
        }

        var transform = EnsureTransform(element);
        var duration = TimeSpan.FromMilliseconds(spec.DurationMs);
        var easing = new CoreEasingFunction(spec.Easing);

        // 先落到起帧，否则会从"当前位置"开始动，看起来像是被拽了一下
        transform.X = spec.FromOffsetX;
        transform.Y = spec.FromOffsetY;
        element.Opacity = spec.FromOpacity;

        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(spec.FromOffsetX, spec.ToOffsetX, duration) { EasingFunction = easing });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(spec.FromOffsetY, spec.ToOffsetY, duration) { EasingFunction = easing });
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(spec.FromOpacity, spec.ToOpacity, duration) { EasingFunction = easing });
    }

    /// <summary>把元素复位到终态（不动画）。</summary>
    public static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        if (element.RenderTransform is TranslateTransform t)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.X = 0;
            t.Y = 0;
        }
    }

    /// <summary>
    /// 在元素显示前先摆到起帧位置，避免"先出现在终点、再跳回起点"的闪烁。
    /// 列表项批量入场时应该在容器 Loaded 之前调用。
    /// </summary>
    public static void PrimeStartState(FrameworkElement element, ElementMotion motion, AnimationSettings? settings = null)
    {
        var spec = AnimationPlanner.Plan(motion, settings ?? Services.AppSettings.Animation);
        if (spec.IsInstant) { Reset(element); return; }

        var transform = EnsureTransform(element);
        transform.X = spec.FromOffsetX;
        transform.Y = spec.FromOffsetY;
        element.Opacity = spec.FromOpacity;
    }

    /// <summary>
    /// 取（或装上）一个 TranslateTransform。
    /// 元素本来就带别的 RenderTransform 时不覆盖它，而是套一层 TransformGroup ——
    /// 直接覆盖会把调用方自己的变换丢掉（例如旋转或缩放）。
    /// </summary>
    private static TranslateTransform EnsureTransform(FrameworkElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform existing:
                return existing;

            case null:
            {
                var t = new TranslateTransform();
                element.RenderTransform = t;
                return t;
            }

            default:
            {
                var group = new TransformGroup();
                group.Children.Add(element.RenderTransform);
                var t = new TranslateTransform();
                group.Children.Add(t);
                element.RenderTransform = group;
                return t;
            }
        }
    }

    /// <summary>
    /// 把 Core 的缓动接到 WPF：**直接调用 Core 的曲线**，
    /// 而不是用 WPF 自带的 QuadraticEase 等近似实现 ——
    /// 否则拖尾手感会与 PCL 有差异，而且两边的数值再也对不上。
    /// </summary>
    private sealed class CoreEasingFunction(EasingKind kind) : EasingFunctionBase
    {
        protected override double EaseInCore(double normalizedTime) => Easing.Of(kind).Ease(normalizedTime);

        // EasingFunctionBase 继承自 Freezable，必须实现这个工厂方法
        // （WPF 在 Freeze / 克隆动画时会用到它）。
        protected override Freezable CreateInstanceCore() => new CoreEasingFunction(kind);
    }
}
