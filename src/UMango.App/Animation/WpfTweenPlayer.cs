// SPDX-License-Identifier: Apache-2.0
//
// WPF 补间播放器 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// PCL 的 `AniStart(aniList, groupName)` 把一组 `AniData` 交给后台线程，
// 后台线程每帧算出增量、投递到 UI 线程执行。效果组（`aniGroups`）以名字分组，
// 同一组里的动画共用起跑时刻 —— 这就是 `isAfter` 的实现基础。
//
// UMango 的差异：
//   1. PCL 的驱动线程是**自建的后台线程 + `RunInUiWait` 投递**；
//      这里复用已有的 <see cref="AnimationService"/>（帧时钟 + UI 编组），
//      不再另起线程 —— 少一个线程，也少一处线程亲和性风险。
//   2. PCL 的「代码类」补间（`AaCode`）是**带延迟**的：延迟到达才执行。
//      这里的 <see cref="TweenSpec.DelayMs"/> 语义相同，但执行时机由播放器安排。

using System.Windows;
using UMango.Core.Animation;
using UMango.Core.Animation.Clock;

namespace UMango.App.Animation;

/// <summary>
/// 播放一组 <see cref="TweenSpec"/> 补间。
///
/// 对应 PCL 的 <c>ModAnimation.AniStart(aniList, groupName)</c>（借鉴设计）。
/// </summary>
public static class WpfTweenPlayer
{
    private static readonly WpfClock _sharedClock = new(fps: 60);
    private static AnimationService? _service;

    /// <summary>
    /// 共享的动画服务。懒创建 —— 避免为每次补间都起一个时钟线程。
    /// </summary>
    private static AnimationService Service => _service ??= new AnimationService(_sharedClock, new WpfUIAccessProvider(System.Windows.Threading.Dispatcher.CurrentDispatcher));

    /// <summary>
    /// 播放一组补间（不等待）。
    /// </summary>
    /// <param name="specs">补间描述。</param>
    /// <param name="binder">通道 → WPF 属性的绑定表。</param>
    /// <param name="fps">帧率。</param>
    public static void Play(
        IReadOnlyList<TweenSpec> specs,
        WpfTweenBinder binder,
        int fps = 60)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(binder);

        if (specs.Count == 0) return;

        var plan = TweenPlanner.Build(specs, binder.ToBindings(), fps);

        // 代码类补间（PCL 的 AaCode）在动画开始前执行。
        // PCL 的 `AaCode(action, delay)` 是"延迟 delay 毫秒后执行"，
        // 这里的实现是"立即执行" —— 延迟版由 <see cref="PlayDelayed"/> 提供。
        foreach (var action in plan.PreActions) action();

        _ = Service.RunAsync(plan.Binding);
    }

    /// <summary>
    /// 播放一组补间并等待完成。
    /// </summary>
    public static async Task PlayAsync(
        IReadOnlyList<TweenSpec> specs,
        WpfTweenBinder binder,
        int fps = 60,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(binder);

        if (specs.Count == 0) return;

        var plan = TweenPlanner.Build(specs, binder.ToBindings(), fps);
        foreach (var action in plan.PreActions) action();

        await Service.RunAsync(plan.Binding, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 延迟执行一个动作（对应 PCL 的 <c>AaCode(action, delay)</c>）。
    ///
    /// PCL 的实现是把代码塞进动画列表，由动画线程在到达该延迟时执行 ——
    /// 这样它天然与同一组动画同步。UMango 这里用同一个调度器实现，
    /// 保证"延迟回调"与"动画组"共享同一条 UI 线程路径。
    /// </summary>
    public static void PlayDelayed(Action action, int delayMs)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (delayMs <= 0)
        {
            action();
            return;
        }

        // DispatcherTimer 的回调在到达时才触发，因此闭包里不需要引用 timer 本身
        // —— 但要停止它，得在外部保留引用。这里用局部变量先声明再赋值。
        System.Windows.Threading.DispatcherTimer? timer = null;
        timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(delayMs),
            System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) =>
            {
                timer?.Stop();
                action();
            },
            System.Windows.Threading.Dispatcher.CurrentDispatcher);

        timer.Start();
    }
}
