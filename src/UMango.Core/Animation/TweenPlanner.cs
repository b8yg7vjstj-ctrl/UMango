// SPDX-License-Identifier: Apache-2.0
//
// 补间编排 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// 原始设计（PCL）：
//   · 每次补间是一份 `AniData`（目标 + 通道 + 数值 + 时长 + 延迟 + 缓动 + 是否排队）。
//   · `AniRun` 每帧算出**增量**（`MathPercent(0, value, ease.GetDelta(...))`），
//     再把这个增量**加到目标的当前值上**（`DeltaLeft` / `Opacity += delta` / `X += delta` …）。
//   · 也就是说：PCL 的补间值是**相对量（增量）**，不是绝对目标值。
//     例：`AaTranslateY(11)` 是"往下挪 11 像素"，不是"挪到 Y=11"。
//
// UMango 的差异：
//   1. PCL 的目标与通道是 `object` + `dynamic` 反射（`((dynamic)obj).Opacity`），
//      离开 WPF 就没法用。这里把"通道 → 目标"变成**显式绑定表**：
//      调用方用 <see cref="TweenChannelBinding"/> 声明每个通道由哪个 <see cref="IAnimatable"/> 承载。
//   2. PCL 的 `AniTypeSub.Double` 走 `obj[0].SetValue(obj[1], …)`（把对象和依赖属性塞进 object[]），
//      这里是 <see cref="TweenSpec.PropertyKey"/>（字符串键）+ 宿主的属性映射表。
//   3. PCL 的「以后」(`isAfter`) 语义是"等同一组里前面的动画跑完"，
//      这里用 <see cref="TweenSpec.Queued"/> 表达，落地为「并行组 → 顺序组」两段。
//
// 数值语义（与 PCL 一致，逐条对应）：
//   · 数值通道：**增量**。终值 = 起始值 + 补间值。
//   · 颜色通道：**增量**。终色 = 起始色 + 补间色。
//   · 缩放通道：**倍率**。终值 = 起始值 × 倍率（PCL 的 AniType.Scale 是改 Margin，
//     这里改成倍率 —— 因为改 Margin 依赖布局，跨框架不可移植；倍率语义等价且更直观）。
//   · 文字出现：**进度计数**。终值 = 文字长度。
//
// 全部计算都是纯数据 → 可以在 macOS 上直接断言每一帧的结果。

using UMango.Core.Animation.Values;

namespace UMango.Core.Animation;

/// <summary>
/// 一个通道与承载它的目标之间的绑定。
///
/// 宿主（WPF / Avalonia）负责把"某个具体属性"包装成 <see cref="IAnimatable"/>，
/// Core 只按绑定表驱动它。
/// </summary>
/// <param name="Channel">通道。</param>
/// <param name="Target">承载该通道数值的对象。</param>
/// <param name="StartValue">
/// 起始值的覆盖项。传 null 表示在编排时**读取目标当前值**（PCL 的行为）。
/// 传非 null 则用它当起点（例如要动画一个"从别的位置来"的元素）。
/// </param>
public sealed record TweenChannelBinding(TweenChannel Channel, IAnimatable Target, object? StartValue = null);

/// <summary>一次补间编排的结果。</summary>
public sealed class TweenPlan
{
    internal TweenPlan(AnimationBinding binding, IReadOnlyList<Action> preActions, IReadOnlyList<TweenSpec> specs)
    {
        Binding = binding;
        PreActions = preActions;
        Specs = specs;
    }

    /// <summary>可直接交给 <see cref="AnimationService"/> 播放的绑定。</summary>
    public AnimationBinding Binding { get; }

    /// <summary>
    /// 播放**前**要执行的动作（对应 PCL 的 <c>AniType.Code</c> 中**不带延迟**的那一类）。
    ///
    /// PCL 的 <c>AaCode(action)</c> 是在动画组启动时立即执行的；
    /// 带延迟的 <c>AaCode(action, delay)</c> 与 <c>after: true</c> 的变体
    /// 则是**动画组内的一个时点**，由 <see cref="Binding"/> 承载（见 <see cref="DelayedActionAnimation"/>）。
    /// </summary>
    public IReadOnlyList<Action> PreActions { get; }

    /// <summary>参与本次编排的补间描述。</summary>
    public IReadOnlyList<TweenSpec> Specs { get; }
}

/// <summary>
/// 把 <see cref="TweenSpec"/> 列表编排成 <see cref="AnimationBinding"/>。
///
/// 与 PCL 的 <c>ModAnimation</c> 组队列对应（借鉴设计）。
/// </summary>
public static class TweenPlanner
{
    /// <summary>
    /// 编排一组补间。
    /// </summary>
    /// <param name="specs">补间描述。</param>
    /// <param name="bindings">通道 → 目标 的绑定表。</param>
    /// <param name="fps">帧率（用于把毫秒换算成帧）。</param>
    /// <param name="registry">值处理器注册表（默认用全套）。</param>
    /// <param name="autoCreateTargets">
    /// 通道在绑定表里找不到时是否**自动创建**一个 <see cref="ValueAnimatable"/>。
    /// 为 true 时，编排结果里会带上自动创建的目标（可通过
    /// <see cref="ResolveTarget"/> 取出），方便宿主把它接到实际属性上。
    /// </param>
    public static TweenPlan Build(
        IReadOnlyList<TweenSpec> specs,
        IReadOnlyList<TweenChannelBinding> bindings,
        int fps,
        ValueProcessorRegistry? registry = null,
        bool autoCreateTargets = true)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(bindings);

        var processors = registry ?? ValueProcessorRegistry.CreateFull();
        var safeFps = Math.Clamp(fps, 1, 1000);

        var targets = new Dictionary<TweenChannel, IAnimatable>();
        var starts = new Dictionary<TweenChannel, object?>();
        foreach (var binding in bindings)
        {
            targets[binding.Channel] = binding.Target;
            starts[binding.Channel] = binding.StartValue;
        }

        var preActions = new List<Action>();
        var parallel = new ParallelAnimationGroup { Name = "Tween:Parallel" };
        var sequential = new SequentialAnimationGroup { Name = "Tween:Sequential" };

        // 同通道的多个补间要**并行叠加**（PCL 语义：每个补间是增量，
        // 同时推进、各自贡献自己的增量），不能后写覆盖先写。
        // 因此先按通道把 spec 归堆，再逐通道决定怎么造动画。
        var byChannel = new Dictionary<TweenChannel, List<TweenSpec>>();
        var order = new List<TweenChannel>();
        foreach (var spec in specs)
        {
            if (spec.Kind == TweenKind.Code) continue;
            if (!byChannel.TryGetValue(spec.Channel, out var list))
            {
                list = [];
                byChannel[spec.Channel] = list;
                order.Add(spec.Channel);
            }
            list.Add(spec);
        }

        foreach (var channel in order)
        {
            var channelSpecs = byChannel[channel];
            var target = targets.GetValueOrDefault(channel);
            if (target is null)
            {
                if (!autoCreateTargets) continue;
                target = new ValueAnimatable();
                targets[channel] = target;
            }

            var startValue = starts.GetValueOrDefault(channel);

            // 排队的补间必须**单独**成组：它要等前面的动画跑完才动，
            // 不能和同通道的不排队补间并行叠加。
            var queuedSpecs = channelSpecs.Where(s => s.Queued).ToList();
            var parallelSpecs = channelSpecs.Where(s => !s.Queued).ToList();

            if (parallelSpecs.Count > 0)
            {
                var animation = BuildChannelAnimation(parallelSpecs, target, startValue, processors, safeFps);
                if (animation is not null) parallel.Add(animation, target);
            }

            foreach (var spec in queuedSpecs)
            {
                var animation = BuildChannelAnimation([spec], target, startValue, processors, safeFps);
                if (animation is not null) sequential.Add(animation, target);
            }
        }

        foreach (var spec in specs)
        {
            if (spec.Kind != TweenKind.Code) continue;

            // 代码类补间没有通道 —— 它是一个"到点执行"的动作。
            //
            // 三种情形（对照 PCL 的 AaCode 三个重载）：
            //   1. 无延迟、不排队 → 动画组启动前立即执行（进 PreActions）。
            //   2. 有延迟、不排队 → 组内一个时点（延迟到达才执行）。
            //   3. 排队（after）→ 排在同组其它动画**之后**执行。
            if (spec.Code is null) continue;

            if (spec.Queued)
            {
                // 排队：放到顺序组的最末尾 —— 它会在前面所有动画演完之后才执行。
                sequential.Add(new DelayedActionAnimation(spec.Code), new ValueAnimatable());
            }
            else if (spec.DelayMs > 0)
            {
                var delayFrames = Math.Max(0, (int)Math.Round(spec.DelayMs / 1000.0 * safeFps));
                parallel.Add(new DelayedActionAnimation(spec.Code, delayFrames), new ValueAnimatable());
            }
            else
            {
                preActions.Add(spec.Code);
            }
        }
        // 顺序组里没有东西时不要把它塞进并行组（否则多一个 0 帧的子项）。
        AnimationBinding result;
        if (sequential.Children.Count == 0)
            result = new AnimationBinding(parallel, parallel.Children.Count > 0 ? parallel.Children[0].Target : new ValueAnimatable());
        else if (parallel.Children.Count == 0)
            result = new AnimationBinding(sequential, sequential.Children[0].Target);
        else
        {
            // 「先并行、后顺序」：PCL 的 isAfter 就是这个意思。
            var outer = new SequentialAnimationGroup { Name = "Tween" };
            outer.Add(parallel, parallel.Children[0].Target);
            outer.Add(sequential, sequential.Children[0].Target);
            result = new AnimationBinding(outer, outer.Children[0].Target);
        }

        return new TweenPlan(result, preActions, specs);
    }

    /// <summary>
    /// 为一个通道上的补间组创建动画。
    ///
    /// 同一通道的多条补间合成**一个** <see cref="TweenChannelAnimation"/> ——
    /// 它们并行推进、各自贡献增量（PCL 语义），而不是互相覆盖。
    /// </summary>
    private static IAnimation? BuildChannelAnimation(
        IReadOnlyList<TweenSpec> specs,
        IAnimatable target,
        object? startOverride,
        ValueProcessorRegistry registry,
        int fps)
    {
        if (specs.Count == 0) return null;

        // 总时长 = 各补间「延迟 + 时长」的最大值。
        var totalMs = specs.Max(s => Math.Max(0, s.DelayMs) + s.EffectiveDurationMs);
        var durationFrames = Math.Max(1, (int)Math.Round(totalMs / 1000.0 * fps));
        return new TweenChannelAnimation(specs, startOverride, durationFrames);
    }

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0d,
    };
}
