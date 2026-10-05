using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UMango.Core.Animation;
using UMango.Core.Animation.Clock;
using UMango.Core.Animation.Values;

namespace UMango.App.Animation;

/// <summary>
/// WPF 动画播放器。
///
/// 支持两种模式：
///   1. <see cref="PlayEntrance"/> —— 旧模式，直接用 WPF 的 DoubleAnimation（已验证可跑，不删）。
///      适用于简单入场动画（位移 + 透明度）。
///   2. <see cref="PlayWithEngine"/> —— 新模式，走 Core 的帧驱动引擎（AnimationService + WpfClock）。
///      适用于复杂动画（关键帧、组合、多属性同时驱动）。
///
/// 两种模式共存而非替换：简单的页面切换用旧模式即可，复杂的动画（将来可能有的
/// 列表项交错、拖拽反馈等）才需要走引擎。这也符合「渐进式迁移」——
/// 不是一次性把所有动画都切换到新引擎（那样风险太高），而是让新引擎先具备
/// 可用性，再逐步扩大使用范围。
/// </summary>
public static class AnimationPlayer
{
    // ================= 旧模式：WPF 原生 DoubleAnimation =================

    /// <summary>播放入场动画（位移 + 淡入）—— 直接用 WPF DoubleAnimation。</summary>
    public static void PlayEntrance(FrameworkElement element, ElementMotion motion, AnimationSettings? settings = null)
    {
        var spec = AnimationPlanner.Plan(motion, settings ?? Services.AppSettings.Animation);
        if (spec.IsInstant) { Reset(element); return; }

        var transform = EnsureTransform(element);
        var duration = TimeSpan.FromMilliseconds(spec.DurationMs);
        var easing = new CoreEasingFunction(spec.Easing);

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

    // ================= 新模式：帧驱动引擎 =================

    private static readonly WpfClock _sharedClock = new(fps: 60);
    private static AnimationService? _service;

    /// <summary>
    /// 共享的动画服务实例。懒创建，页面切换都用它。
    /// 为什么共享而不是每次创建：避免为每个简单入场动画都起一个时钟线程。
    /// </summary>
    private static AnimationService Service => _service ??= new AnimationService(_sharedClock);

    /// <summary>
    /// 用 Core 帧驱动引擎播放入场动画。
    ///
    /// 与 PlayEntrance 的区别：
    ///   - PlayEntrance 直接用 WPF 的 DoubleAnimation，无法测试关键帧/组合/中途取消
    ///   - PlayWithEngine 走 Core 的 AnimationService，支持关键帧、Sequential/Parallel 组合、
    ///     取消令牌、以及将来的更复杂动画（拖拽反馈、列表交错入场等）
    ///
    /// 返回 Task：动画完成（或取消）后完成，调用方可 await。
    /// </summary>
    public static async Task PlayWithEngineAsync(
        FrameworkElement element, ElementMotion motion,
        AnimationSettings? settings = null, CancellationToken ct = default)
    {
        var eff = settings ?? Services.AppSettings.Animation;
        var (x, y, o) = WpfAnimatable.FromElement(element);

        if (TryBuildBinding(motion, eff, x, y, o, out var binding))
        {
            await Service.RunAsync(binding!, ct);
            return;
        }

        var spec = AnimationPlanner.Plan(motion, eff);
        if (spec.IsInstant)
        {
            ApplyInstantState(element, spec);
            return;
        }
        binding = AnimationPlannerBinding.CreateEntrance(spec, x, y, o, fps: 60);
        await Service.RunAsync(binding, ct);
    }

    /// <summary>用帧驱动引擎播放任意动画绑定（关键帧、组合、自定义动画的入口）。</summary>
    public static async Task PlayWithEngineAsync(AnimationBinding binding, CancellationToken ct = default)
    {
        await Service.RunAsync(binding, ct);
    }

    /// <summary>同步版：用于不能 await 的场景（Loaded 事件处理器）。</summary>
    public static void PlayWithEngineFireAndForget(
        FrameworkElement element, ElementMotion motion, AnimationSettings? settings = null)
    {
        var eff = settings ?? Services.AppSettings.Animation;
        var (x, y, o) = WpfAnimatable.FromElement(element);

        if (TryBuildBinding(motion, eff, x, y, o, out var binding))
        {
            Service.RunFireAndForget(binding!);
            return;
        }

        var spec = AnimationPlanner.Plan(motion, eff);
        if (spec.IsInstant) { ApplyInstantState(element, spec); return; }
        binding = AnimationPlannerBinding.CreateEntrance(spec, x, y, o, fps: 60);
        Service.RunFireAndForget(binding);
    }

    // ================= 共用工具 =================

    /// <summary>
    /// 尝试为页面切换构建 PCL 式关键帧组合（淡入 + 两段落体回弹 + 无 X 位移）。
    /// 返回 false 表示该 motion 不适用（列表项/对话框等仍走单段 Plan）。
    ///
    /// 动画关掉时也不走这里 —— 关掉动画应立即到终态，单段 Plan 的 IsInstant 路径处理更直接，
    /// 所以 enabled=false 时同样返回 false。
    /// </summary>
    private static bool TryBuildBinding(
        ElementMotion motion, AnimationSettings settings,
        UMango.Core.Animation.IAnimatable x, UMango.Core.Animation.IAnimatable y, UMango.Core.Animation.IAnimatable o,
        out AnimationBinding? binding)
    {
        binding = null;
        if (motion != ElementMotion.PageContent) return false;

        // 路径选择下沉到 Core 的 AnimationRouting（纯逻辑、可测）：
        // 仅 PCL 预设走组合，Fluent10 走经典单段 —— 理由见该类的注释。
        if (!AnimationRouting.UsePclPageCombo(settings)) return false;

        binding = AnimationPlannerBinding.CreatePageEnter(
            PageMotionProfile.Pcl, x, y, o, settings, fps: 60);
        return true;
    }

    private static void ApplyInstantState(FrameworkElement element, AnimationSpec spec)
    {
        var transform = EnsureTransform(element);
        transform.X = spec.ToOffsetX;
        transform.Y = spec.ToOffsetY;
        element.Opacity = spec.ToOpacity;
    }

    public static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (element.RenderTransform is TranslateTransform t)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.X = 0; t.Y = 0;
        }
    }

    public static void PrimeStartState(FrameworkElement element, ElementMotion motion, AnimationSettings? settings = null)
    {
        var spec = AnimationPlanner.Plan(motion, settings ?? Services.AppSettings.Animation);
        if (spec.IsInstant) { Reset(element); return; }
        var transform = EnsureTransform(element);
        transform.X = spec.FromOffsetX;
        transform.Y = spec.FromOffsetY;
        element.Opacity = spec.FromOpacity;
    }

    private static TranslateTransform EnsureTransform(FrameworkElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform existing: return existing;
            case null: var t1 = new TranslateTransform(); element.RenderTransform = t1; return t1;
            default:
                var group = new TransformGroup();
                group.Children.Add(element.RenderTransform);
                var t = new TranslateTransform();
                group.Children.Add(t);
                element.RenderTransform = group;
                return t;
        }
    }

    /// <summary>Core 的缓动曲线接到 WPF。直接调用 Core 的 Easing，不用 WPF 自带近似。</summary>
    private sealed class CoreEasingFunction(EasingKind kind) : EasingFunctionBase
    {
        protected override double EaseInCore(double normalizedTime)
            => Easing.Of(kind).Ease(normalizedTime);
        protected override Freezable CreateInstanceCore() => new CoreEasingFunction(kind);
    }
}
