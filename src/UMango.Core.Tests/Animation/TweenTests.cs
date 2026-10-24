// SPDX-License-Identifier: Apache-2.0
//
// 补间工厂与编排的回归测试。
//
// 对应 PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModAnimation.cs`（受限许可目录，
// 这里只借鉴其设计语义并断言等价行为）。
//
// 关键语义（逐条对照 PCL 的 AniRun）：
//   1. 数值通道是**增量**，不是绝对目标值。
//   2. 颜色通道是**增量**。
//   3. 缩放是**倍率**。
//   4. 「以后」(`isAfter`/`Queued`) 的动画排在前面的并行组之后。
//   5. 文字出现的终值是**字符数**。

using UMango.Core.Animation;
using UMango.Core.Animation.Values;
using Xunit;

namespace UMango.Core.Tests.Animation;

public class TweenFactoryTests
{
    [Fact]
    public void 数值补间的默认参数与PCL一致()
    {
        var spec = TweenFactory.AaX(12);
        Assert.Equal(TweenKind.Number, spec.Kind);
        Assert.Equal(TweenChannel.X, spec.Channel);
        Assert.Equal(12, spec.Value);
        Assert.Equal(400, spec.DurationMs);
        Assert.Equal(0, spec.DelayMs);
        Assert.False(spec.Queued);
        Assert.Null(spec.Easing);
    }

    [Theory]
    [InlineData(TweenChannel.X)]
    [InlineData(TweenChannel.Y)]
    [InlineData(TweenChannel.Width)]
    [InlineData(TweenChannel.Height)]
    [InlineData(TweenChannel.Opacity)]
    [InlineData(TweenChannel.Value)]
    [InlineData(TweenChannel.Radius)]
    [InlineData(TweenChannel.BorderThickness)]
    [InlineData(TweenChannel.StrokeThickness)]
    [InlineData(TweenChannel.GridLengthWidth)]
    [InlineData(TweenChannel.TranslateX)]
    [InlineData(TweenChannel.TranslateY)]
    public void 各个数值工厂映射到正确的通道(TweenChannel channel)
    {
        var spec = channel switch
        {
            TweenChannel.X => TweenFactory.AaX(1),
            TweenChannel.Y => TweenFactory.AaY(1),
            TweenChannel.Width => TweenFactory.AaWidth(1),
            TweenChannel.Height => TweenFactory.AaHeight(1),
            TweenChannel.Opacity => TweenFactory.AaOpacity(1),
            TweenChannel.Value => TweenFactory.AaValue(1),
            TweenChannel.Radius => TweenFactory.AaRadius(1),
            TweenChannel.BorderThickness => TweenFactory.AaBorderThickness(1),
            TweenChannel.StrokeThickness => TweenFactory.AaStrokeThickness(1),
            TweenChannel.GridLengthWidth => TweenFactory.AaGridLengthWidth(1),
            TweenChannel.TranslateX => TweenFactory.AaTranslateX(1),
            TweenChannel.TranslateY => TweenFactory.AaTranslateY(1),
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(channel, spec.Channel);
        Assert.Equal(TweenKind.Number, spec.Kind);
    }

    [Fact]
    public void 双精度工厂带属性键()
    {
        var spec = TweenFactory.AaDouble("BorderThickness", 3, 200, 50);
        Assert.Equal(TweenChannel.Double, spec.Channel);
        Assert.Equal("BorderThickness", spec.PropertyKey);
        Assert.Equal(200, spec.DurationMs);
        Assert.Equal(50, spec.DelayMs);
    }

    [Fact]
    public void 颜色补间是颜色种类()
    {
        var spec = TweenFactory.AaColor(new NColor(10, 20, 30, 40));
        Assert.Equal(TweenKind.Color, spec.Kind);
        Assert.Equal(TweenChannel.Color, spec.Channel);
        Assert.Equal(new NColor(10, 20, 30, 40), spec.Color);
    }

    [Fact]
    public void 缩放补间带倍率与基准尺寸()
    {
        var spec = TweenFactory.AaScale(1.5, baseSize: (200, 100));
        Assert.Equal(TweenKind.Scale, spec.Kind);
        Assert.Equal(1.5, spec.Scale);
        Assert.Equal((200d, 100d), spec.ScaleBaseSize);
    }

    [Fact]
    public void 文字逐字出现的总时长是每字时长乘字数()
    {
        var spec = TweenFactory.AaTextAppear("你好世界", timePerTextMs: 70);
        Assert.Equal(TweenKind.TextAppear, spec.Kind);
        Assert.Equal(4, spec.Text!.Length);
        Assert.Equal(280, spec.DurationMs);
    }

    [Fact]
    public void 文字消失标记被保留()
    {
        var spec = TweenFactory.AaTextAppear("abc", hide: true);
        Assert.True(spec.TextHide);
    }

    [Fact]
    public void 代码补间保留动作与极短时长()
    {
        var ran = false;
        var spec = TweenFactory.AaCode(() => ran = true);
        Assert.Equal(TweenKind.Code, spec.Kind);
        Assert.NotNull(spec.Code);
        spec.Code!();
        Assert.True(ran);
        // 时长 1ms —— 对应 PCL 的 timeTotal = 1
        Assert.Equal(1, spec.DurationMs);
    }

    [Fact]
    public void 排队标记被保留()
    {
        Assert.True(TweenFactory.AaX(1, after: true).Queued);
        Assert.False(TweenFactory.AaX(1).Queued);
    }
}

public class TweenPlannerTests
{
    private static IAnimatable Target(double initial = 0) => new ValueAnimatable(initial);

    [Fact]
    public void 数值补间是增量而非绝对值()
    {
        // PCL 的 AaTranslateY(11) 语义是"往下挪 11"，不是"挪到 Y=11"。
        var target = Target(100);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaY(11, time: 100)],
            [new TweenChannelBinding(TweenChannel.Y, target)],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(111d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 数值补间从零开始时终值等于补间值()
    {
        var target = Target(0);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaOpacity(0.5, time: 100)],
            [new TweenChannelBinding(TweenChannel.Opacity, target)],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(0.5d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 起始值可被覆盖()
    {
        var target = Target(999);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaX(5, time: 100)],
            [new TweenChannelBinding(TweenChannel.X, target, StartValue: 0d)],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(5d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 颜色补间是增量()
    {
        var target = new ValueAnimatable(new NColor(10, 10, 10, 255));
        var plan = TweenPlanner.Build(
            [TweenFactory.AaColor(new NColor(5, 0, 0, 0), time: 100)],
            [new TweenChannelBinding(TweenChannel.Color, target)],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        var result = Assert.IsType<NColor>(target.GetValue());
        Assert.Equal(15f, result.R, 3);
        Assert.Equal(10f, result.G, 3);
    }

    [Fact]
    public void 缩放补间是倍率()
    {
        var target = Target(200);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaScale(0.5, time: 100)],
            [new TweenChannelBinding(TweenChannel.Scale, target)],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(100d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 文字出现的终值是字符数()
    {
        var target = Target(0);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaTextAppear("abcd", timePerTextMs: 25)],
            [new TweenChannelBinding(TweenChannel.TextAppear, target)],
            fps: 40);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(4d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 文字消失的终值是零()
    {
        var target = Target(4);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaTextAppear("abcd", hide: true, timePerTextMs: 25)],
            [new TweenChannelBinding(TweenChannel.TextAppear, target)],
            fps: 40);

        plan.Binding.Animation.ApplyFrame(plan.Binding.Animation.DurationFrames, target);

        Assert.Equal(0d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 代码补间不进入动画而进入前置动作()
    {
        var ran = false;
        var target = Target(0);
        var plan = TweenPlanner.Build(
            [TweenFactory.AaCode(() => ran = true)],
            [new TweenChannelBinding(TweenChannel.X, target)],
            fps: 10);

        Assert.Single(plan.PreActions);
        Assert.False(ran);

        plan.PreActions[0]();
        Assert.True(ran);
    }

    [Fact]
    public void 排队的补间排在前面的并行组之后()
    {
        var first = Target(0);
        var second = Target(0);

        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaY(20, time: 100, after: true),
            ],
            [
                new TweenChannelBinding(TweenChannel.X, first),
                new TweenChannelBinding(TweenChannel.Y, second),
            ],
            fps: 10);

        // 100ms @ 10fps = 1 帧。并行组 1 帧 + 顺序组 1 帧 = 2 帧。
        // 关键是**排队的补间不在并行组里** —— 它排在并行组演完之后。
        var outer = Assert.IsType<SequentialAnimationGroup>(plan.Binding.Animation);
        Assert.Equal(2, outer.DurationFrames);
        Assert.IsType<ParallelAnimationGroup>(outer.Children[0].Animation);
        Assert.IsType<SequentialAnimationGroup>(outer.Children[1].Animation);
    }

    [Fact]
    public void 排队的补间在并行组演完之前不生效()
    {
        var first = Target(0);
        var second = Target(0);

        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaY(20, time: 100, after: true),
            ],
            [
                new TweenChannelBinding(TweenChannel.X, first),
                new TweenChannelBinding(TweenChannel.Y, second),
            ],
            fps: 10);

        // 第 0 帧：并行组里的 X 起步，排队中的 Y 还没开始。
        plan.Binding.Animation.ApplyFrame(0, first);
        Assert.Equal(0d, Convert.ToDouble(second.GetValue()), 6);
    }

    [Fact]
    public void 找不到通道绑定时自动创建目标()
    {
        var plan = TweenPlanner.Build(
            [TweenFactory.AaX(7, time: 100)],
            [],
            fps: 10);

        Assert.NotNull(plan.Binding.Animation);
        Assert.True(plan.Binding.Animation.DurationFrames > 0);
    }

    [Fact]
    public void 关闭自动创建时找不到绑定的补间被跳过()
    {
        var plan = TweenPlanner.Build(
            [TweenFactory.AaX(7, time: 100)],
            [],
            fps: 10,
            autoCreateTargets: false);

        Assert.Equal(0, plan.Binding.Animation.DurationFrames);
    }
    [Fact]
    public void 延迟代码补间进入动画组而不是前置动作()
    {
        var ran = false;
        var plan = TweenPlanner.Build(
            [TweenFactory.AaCode(() => ran = true, delay: 300)],
            [new TweenChannelBinding(TweenChannel.X, Target(0))],
            fps: 10);

        // 带延迟的代码类补间是**动画组里的一个时点**，不是启动前的前置动作。
        Assert.Empty(plan.PreActions);
        Assert.False(ran);
    }

    [Fact]
    public void 延迟代码补间在到达延迟之前不执行()
    {
        var ran = false;
        var plan = TweenPlanner.Build(
            [TweenFactory.AaCode(() => ran = true, delay: 300)],
            [new TweenChannelBinding(TweenChannel.X, Target(0))],
            fps: 10);

        var target = Target(0);
        var delayed = Assert.IsType<DelayedActionAnimation>(FindDelayed(plan.Binding.Animation));

        // 300ms @ 10fps = 3 帧延迟 → 总共 4 帧。
        Assert.Equal(4, delayed.DurationFrames);

        delayed.ApplyFrame(2, target);
        Assert.False(ran);

        delayed.ApplyFrame(3, target);
        Assert.True(ran);
    }

    [Fact]
    public void 延迟代码补间只执行一次()
    {
        var count = 0;
        var plan = TweenPlanner.Build(
            [TweenFactory.AaCode(() => count++, delay: 100)],
            [new TweenChannelBinding(TweenChannel.X, Target(0))],
            fps: 10);

        var target = Target(0);
        var delayed = Assert.IsType<DelayedActionAnimation>(FindDelayed(plan.Binding.Animation));

        delayed.ApplyFrame(1, target);
        delayed.ApplyFrame(1, target);
        delayed.ApplyFrame(2, target);

        Assert.Equal(1, count);
    }

    [Fact]
    public void 排队的代码补间排在同组动画之后执行()
    {
        var order = new List<string>();
        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaCode(() => order.Add("code"), after: true),
            ],
            [new TweenChannelBinding(TweenChannel.X, Target(0))],
            fps: 10);

        // 排队的代码类补间不进 PreActions，而是排进顺序组。
        Assert.Empty(plan.PreActions);
        Assert.Equal(1, plan.Binding.Animation.DurationFrames);
    }

    [Fact]
    public void 排队的代码补间在动画演完之后才执行()
    {
        var order = new List<string>();
        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaCode(() => order.Add("code"), after: true),
            ],
            [
                new TweenChannelBinding(TweenChannel.X, Target(0)),
            ],
            fps: 10);

        var target = Target(0);
        var outer = Assert.IsType<SequentialAnimationGroup>(plan.Binding.Animation);
        var parallel = Assert.IsType<ParallelAnimationGroup>(outer.Children[0].Animation);
        var sequential = Assert.IsType<SequentialAnimationGroup>(outer.Children[1].Animation);

        // 只演并行组 → 排队的代码还没执行。
        parallel.ApplyFrame(0, target);
        Assert.Empty(order);

        // 顺序组里只有那个延迟动作。
        var delayed = Assert.IsType<DelayedActionAnimation>(sequential.Children[0].Animation);
        delayed.ApplyFrame(0, target);
        Assert.Equal(["code"], order);
    }

    /// <summary>在动画组树里找到第一个延迟动作（测试辅助）。</summary>
    private static DelayedActionAnimation FindDelayed(IAnimation animation)
    {
        switch (animation)
        {
            case DelayedActionAnimation delayed:
                return delayed;
            case AnimationGroup group:
                foreach (var child in group.Children)
                {
                    var found = FindDelayed(child.Animation);
                    if (found is not null) return found;
                }
                break;
        }
        return null!;
    }
}

/// <summary>
/// 通道级补间的回归测试 —— 覆盖两个曾被实现的真实缺陷：
///   1. <c>DelayMs</c> 被整个丢弃（数值补间一律从第 0 帧就动）。
///   2. 同通道的多个补间**后写覆盖先写**（PCL 的"并行叠加"语义丢失）。
/// </summary>
public class TweenChannelAnimationTests
{
    private static ValueAnimatable Target(double value = 0) => new(value);

    [Fact]
    public void 延迟未到时补间不产生任何位移()
    {
        var target = Target(0);
        var spec = TweenFactory.AaX(10, time: 100, delay: 100);
        var animation = new TweenChannelAnimation([spec], null, durationFrames: 2);

        // 总时长 = 延迟 100ms + 时长 100ms = 200ms。
        Assert.Equal(200, animation.TotalDurationMs);

        // 第 0 帧（时刻 0）：延迟未到 → 贡献 0。
        animation.ApplyFrame(0, target);
        Assert.Equal(0d, Convert.ToDouble(target.GetValue()), 6);

        // 第 1 帧（时刻 100ms）：延迟刚好到 → 该帧仍在起点。
        animation.ApplyFrame(1, target);
        Assert.Equal(0d, Convert.ToDouble(target.GetValue()), 6);

        // 第 2 帧（时刻 200ms）：延迟已过且动画结束 → 满额位移。
        animation.ApplyFrame(2, target);
        Assert.Equal(10d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 延迟的补间总时长把延迟算进去()
    {
        var animation = new TweenChannelAnimation(
            [TweenFactory.AaX(10, time: 100, delay: 300)], null, durationFrames: 4);
        Assert.Equal(400, animation.TotalDurationMs);
    }

    [Fact]
    public void 同一通道的多个补间是叠加而不是覆盖()
    {
        // PCL 的入场位移：初值 -16，段1 +5/250ms，段2 +11/350ms。
        // 终态 = -16 + 5 + 11 = 0。
        var target = Target(-16);
        var animation = new TweenChannelAnimation(
            [
                TweenFactory.AaTranslate(TweenChannel.TranslateY, 5, time: 250),
                TweenFactory.AaTranslate(TweenChannel.TranslateY, 11, time: 350),
            ],
            null,
            durationFrames: 21);

        animation.ApplyFrame(21, target);
        Assert.Equal(0d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 同一通道的补间在中途各自贡献()
    {
        // 两段都是线性、各 100ms，各自在自己的时间窗内爬升。
        var target = Target(0);
        var animation = new TweenChannelAnimation(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaX(20, time: 200),
            ],
            null,
            durationFrames: 20);

        // 总时长 = 200ms。第 10 帧 = 100ms：
        //   段1 已完成 → 贡献 10
        //   段2 走了一半 → 贡献 10
        animation.ApplyFrame(10, target);
        Assert.Equal(20d, Convert.ToDouble(target.GetValue()), 6);

        // 终点：10 + 20 = 30。
        animation.ApplyFrame(20, target);
        Assert.Equal(30d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 叠加时不会逐帧累加()
    {
        // 起始值只抓一次 —— 若每帧重读目标当前值，就会变成 1、2、3… 的螺旋上升。
        var target = Target(0);
        var animation = new TweenChannelAnimation(
            [TweenFactory.AaX(10, time: 100)], null, durationFrames: 10);

        for (var f = 0; f <= 10; f++) animation.ApplyFrame(f, target);

        Assert.Equal(10d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 起始值覆盖时基准是被覆盖的值()
    {
        var target = Target(999);
        var animation = new TweenChannelAnimation(
            [TweenFactory.AaX(10, time: 100)], -16, durationFrames: 10);

        animation.ApplyFrame(10, target);
        Assert.Equal(-6d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 多个补间的通道各自独立()
    {
        var x = Target(0);
        var y = Target(0);
        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaY(20, time: 100),
            ],
            [
                new TweenChannelBinding(TweenChannel.X, x),
                new TweenChannelBinding(TweenChannel.Y, y),
            ],
            fps: 10);

        plan.Binding.Animation.ApplyFrame(10, x);
        Assert.Equal(10d, Convert.ToDouble(x.GetValue()), 6);
        Assert.Equal(20d, Convert.ToDouble(y.GetValue()), 6);
    }

    [Fact]
    public void 编排时同通道的补间合成一个动画()
    {
        var target = Target(0);
        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(5, time: 100),
                TweenFactory.AaX(11, time: 100),
            ],
            [new TweenChannelBinding(TweenChannel.X, target)],
            fps: 10);

        var group = Assert.IsType<ParallelAnimationGroup>(plan.Binding.Animation);
        Assert.Single(group.Children);

        group.ApplyFrame(10, target);
        Assert.Equal(16d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 不透明度被夹在0到1之间()
    {
        // PCL 的 AniRun 对 Opacity 用 MathClamp(current + delta, 0d, 1d)。
        // 少了这一步，过冲的补间会让不透明度超过 1（WPF 会抛异常）。
        var target = Target(0);
        var animation = new TweenChannelAnimation(
            [TweenFactory.AaOpacity(1.5, time: 100)], null, durationFrames: 10);

        animation.ApplyFrame(10, target);
        Assert.Equal(1d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 不透明度不会低于零()
    {
        var target = Target(0.2);
        var animation = new TweenChannelAnimation(
            [TweenFactory.AaOpacity(-1, time: 100)], null, durationFrames: 10);

        animation.ApplyFrame(10, target);
        Assert.Equal(0d, Convert.ToDouble(target.GetValue()), 6);
    }

    [Fact]
    public void 排队的补间完全不参与并行组()
    {
        // 排队的补间必须**只**在顺序组里跑。
        // 若它同时被塞进并行组，那么并行组演完的那一刻它就已经动过了 ——
        // 而 PCL 的语义是"等前面的动画跑完再跑"。
        var x = Target(0);
        var y = Target(0);
        var plan = TweenPlanner.Build(
            [
                TweenFactory.AaX(10, time: 100),
                TweenFactory.AaY(20, time: 100, after: true),
            ],
            [
                new TweenChannelBinding(TweenChannel.X, x),
                new TweenChannelBinding(TweenChannel.Y, y),
            ],
            fps: 10);

        var outer = Assert.IsType<SequentialAnimationGroup>(plan.Binding.Animation);
        var parallel = Assert.IsType<ParallelAnimationGroup>(outer.Children[0].Animation);

        // 并行组里**只有** X 那一条。
        Assert.Single(parallel.Children);

        // 并行组演完（最后一帧）时，Y 必须还是起点。
        parallel.ApplyFrame(parallel.DurationFrames, x);
        Assert.Equal(0d, Convert.ToDouble(y.GetValue()), 6);
    }
}
