// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 补间播放器 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// PCL 的 `ModAnimation.AniStart(aniList, groupName)` 把一组 `AniData` 交给
// 后台线程，后台线程每帧算增量、投递到 UI 线程执行。
//
// UMango 的差异：复用 Core 的 <see cref="AnimationService"/>（帧时钟 + UI 编组），
// 不另起线程 —— 少一个线程，也少一处线程亲和性风险。

using Avalonia.Threading;
using UMango.Core.Animation;
using UMango.Core.Animation.Clock;

namespace UMango.Avalonia;

/// <summary>播放一组 <see cref="TweenSpec"/> 补间（Avalonia 侧）。</summary>
public static class AvaloniaTweenPlayer
{
    private static readonly StopwatchClock _sharedClock = new(fps: 60);
    private static AnimationService? _service;

    /// <summary>共享的动画服务。懒创建 —— 避免为每次补间都起一个时钟线程。</summary>
    private static AnimationService Service => _service ??= new AnimationService(
        _sharedClock,
        new AvaloniaUIAccessProvider(Dispatcher.UIThread));

    /// <summary>播放一组补间（不等待）。</summary>
    public static void Play(
        IReadOnlyList<TweenSpec> specs,
        AvaloniaTweenBinder binder,
        int fps = 60)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(binder);

        if (specs.Count == 0) return;

        var plan = TweenPlanner.Build(specs, binder.ToBindings(), fps);
        foreach (var action in plan.PreActions) action();
        _ = Service.RunAsync(plan.Binding);
    }

    /// <summary>播放一组补间并等待完成。</summary>
    public static async Task PlayAsync(
        IReadOnlyList<TweenSpec> specs,
        AvaloniaTweenBinder binder,
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
}
