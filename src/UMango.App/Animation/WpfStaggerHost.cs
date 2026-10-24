// SPDX-License-Identifier: Apache-2.0
//
// 页面级联动画宿主 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：
//   · Plain Craft Launcher 2/Controls/MyPageRight.cs:552-600（入场）
//   · Plain Craft Launcher 2/Controls/MyPageLeft.cs:139-166（收集规则）
//   · Plain Craft Launcher 2/Controls/MyCard.cs:305（AaCode 收尾）
// （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
// 不复制其代码；本文件为独立重写。
//
// PCL 的页面入场是三层叠加：
//   1. **整页**先做一次（整页淡入 + 从 -16 滑入）；
//   2. 然后**逐个子项**级联（每个子项 delay += 25ms）；
//   3. 每个子项的动画跑完后，代码收尾（把 Height 之类还原成实际值）。
//
// UMango 的差异：
//   · PCL 用 `dynamic` 反射直接改 WPF 属性；这里用 Core 的
//     <see cref="TweenSpec"/> 纯数据 + <see cref="WpfTweenBinder"/> 绑定表，
//     同一套数值可跨框架复用。
//   · PCL 的「整页」与「子项」用同一个 MyPageRight 驱动；这里把两者拆成
//     <see cref="WpfStaggerHost.PlayPageEnter"/>（整页）与
//     <see cref="WpfStaggerHost.PlayItemEnter"/>（子项级联）两个入口。

using System.Windows;
using System.Windows.Media;
using UMango.Core.Animation;

namespace UMango.App.Animation;

/// <summary>
/// 页面级联动画宿主：把「收集可动画控件 → 编排级联 → 播放」这条链路接起来。
/// </summary>
public static class WpfStaggerHost
{
    /// <summary>
    /// 播放**整页**的入场动画（PCL 的 <c>MyPageRight.TriggerEnterAnimation</c>）。
    ///
    /// PCL 的整页动画只有三条补间：不透明度 +1（100ms）、Y 位移 +5（250ms）、
    /// Y 位移 +11（350ms）。注意位移是**增量**（从 -16 回到 0）。
    /// </summary>
    /// <param name="page">页面根元素。</param>
    /// <param name="delayMs">级联延迟（整页通常是 0）。</param>
    /// <param name="enabled">动画是否启用（关掉时直接落到终态）。</param>
    public static void PlayPageEnter(FrameworkElement page, int delayMs = 0, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!enabled)
        {
            ResetToRest(page);
            return;
        }

        // 初始态：透明 + 从上方 -16 处。
        page.Opacity = 0d;
        EnsureTranslate(page).Y = PclTweenPresets.EnterFromY;

        var specs = new List<TweenSpec>
        {
            TweenFactory.AaOpacity(1d, PclTweenPresets.FadeInMs, delayMs, PclTweenPresets.FadeInEase),
            TweenFactory.AaTranslate(TweenChannel.TranslateY, PclTweenPresets.EnterMidDelta,
                PclTweenPresets.EnterMidMs, delayMs, PclTweenPresets.EnterMidEase),
            TweenFactory.AaTranslate(TweenChannel.TranslateY, PclTweenPresets.EnterToDelta,
                PclTweenPresets.EnterToMs, delayMs, PclTweenPresets.EnterToEase),
        };

        PlayOn(page, specs);
    }

    /// <summary>
    /// 播放**子项级联**的入场动画（PCL 的子项部分）。
    ///
    /// PCL 的子项规则与整页相同（同样的三条补间、同样的初始态），
    /// 区别只有延迟：每个子项 delay += 25ms。
    /// </summary>
    /// <param name="items">子项（已按视觉顺序排好）。</param>
    /// <param name="axis">级联方向（右栏 = 纵向，左栏 = 横向）。</param>
    /// <param name="profile">档位参数（默认 PCL 原版）。</param>
    /// <param name="enabled">动画是否启用。</param>
    /// <param name="translateEnabled">
    /// 是否让子项也做位移。
    ///
    /// 当**整页**已经在做 -16 → +5 → +11 的位移时，子项再各自做一次就会
    /// 叠加成双倍位移（PCL 没有这个问题：它的整页与子项是两个独立元素）。
    /// 这种情况下传 false，只保留子项的淡入与级联延迟。
    /// </param>
    /// <returns>实际参与动画的子项数。</returns>
    public static int PlayItemEnter(
        IReadOnlyList<FrameworkElement> items,
        StaggerAxis axis = StaggerAxis.Vertical,
        StaggerProfile? profile = null,
        bool enabled = true,
        bool translateEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        profile ??= axis == StaggerAxis.Vertical ? StaggerProfile.PclRight : StaggerProfile.PclLeft;

        // 逐项可见性：PCL 是**任意**跳过不可见项（折叠的子项夹在中间也照样跳过），
        // 所以用按索引的重载，而不是"前 N 项可见"。
        var plans = StaggerPlanner.Build(items.Count, axis, StaggerPhase.Enter, profile,
            i => items[i].Visibility != Visibility.Collapsed, null);

        var played = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var plan = plans[i];
            if (!plan.Visible) continue;

            if (!enabled)
            {
                ResetToRest(item);
                continue;
            }

            // 初始态。
            item.Opacity = plan.FromOpacity;
            if (translateEnabled)
            {
                var translate = EnsureTranslate(item);
                if (axis == StaggerAxis.Vertical) translate.Y = plan.FromOffset;
                else translate.X = plan.FromOffset;
            }

            // 不做位移时只留淡入那一条（位移通道是增量，去掉才不会残留位移）。
            var specs = translateEnabled
                ? plan.Specs
                : plan.Specs.Where(s => s.Channel == TweenChannel.Opacity).ToList();

            PlayOn(item, specs);
            played++;
        }

        return played;
    }

    /// <summary>
    /// 播放**子项级联**的退场动画（PCL 的 <c>MyPageLeft.TriggerHideAnimation</c>）。
    /// </summary>
    public static int PlayItemExit(
        IReadOnlyList<FrameworkElement> items,
        StaggerAxis axis = StaggerAxis.Horizontal,
        StaggerProfile? profile = null,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;

        profile ??= axis == StaggerAxis.Horizontal ? StaggerProfile.PclLeft : StaggerProfile.PclRight;

        var plans = StaggerPlanner.Build(items.Count, axis, StaggerPhase.Exit, profile,
            i => items[i].Visibility != Visibility.Collapsed, null);

        var played = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var plan = plans[i];
            if (!plan.Visible) continue;

            if (!enabled)
            {
                item.Opacity = 0d;
                continue;
            }

            PlayOn(item, plan.Specs);
            played++;
        }

        return played;
    }

    /// <summary>
    /// 把元素复位到「静止态」（不透明、无位移）—— 动画关闭或被取消时的兜底。
    /// </summary>
    public static void ResetToRest(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.Opacity = 1d;
        var translate = EnsureTranslate(element);
        translate.X = 0d;
        translate.Y = 0d;
    }

    /// <summary>
    /// 在一个元素上播放一组补间（把通道接到该元素的默认绑定表）。
    /// </summary>
    private static void PlayOn(FrameworkElement element, IReadOnlyList<TweenSpec> specs)
    {
        var binder = WpfTweenBinder.ForElement(element);
        WpfTweenPlayer.Play(specs, binder);
    }

    /// <summary>
    /// 取（必要时创建）元素的 <see cref="TranslateTransform"/>。
    ///
    /// PCL 用 `RenderTransform` 上的 `TranslateTransform`；若元素已经有别的变换
    /// （例如 ScaleTransform），这里**不覆盖** —— 直接复用会导致动画丢失。
    /// </summary>
    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }
}
