// SPDX-License-Identifier: Apache-2.0
//
// PCL 页面动效预设的回归测试。
//
// 对照源：PCL-CE `Plain Craft Launcher 2/Controls/MyPageRight.cs:552-600`
// （受限许可目录 → 这里只断言**公开的数值与语义**，不复制其代码）。
//
// 这些测试的价值在于：任何人对 PCL 数值做了"顺手改一下"的优化，
// 都会在这里被抓出来 —— 需求原文要求「动效在 PCL 主题中完整保留」。

using UMango.Core.Animation;

namespace UMango.Core.Tests.Animation;

/// <summary>
/// PCL 页面入场动效的数值与语义断言。
/// </summary>
public class PclTweenPresetTests
{
    [Fact]
    public void 入场是三条补间_淡入加两段落体()
    {
        var specs = PclTweenPresets.PageEnter();

        Assert.Equal(3, specs.Count);
        Assert.Equal(TweenChannel.Opacity, specs[0].Channel);
        Assert.Equal(TweenChannel.TranslateY, specs[1].Channel);
        Assert.Equal(TweenChannel.TranslateY, specs[2].Channel);
    }

    [Fact]
    public void 入场数值与PCL原版一致()
    {
        var specs = PclTweenPresets.PageEnter();

        // PCL: AaOpacity(Control, 1d, 100, delay, AniEaseOutFluent(Weak))
        Assert.Equal(1d, specs[0].Value);
        Assert.Equal(100, specs[0].DurationMs);

        // PCL: AaTranslateY(Control, 5d, 250, delay, AniEaseOutFluent())
        Assert.Equal(5d, specs[1].Value);
        Assert.Equal(250, specs[1].DurationMs);

        // PCL: AaTranslateY(Control, 11d, 350, delay, AniEaseOutBack())
        Assert.Equal(11d, specs[2].Value);
        Assert.Equal(350, specs[2].DurationMs);
    }

    [Fact]
    public void 三条补间共享同一个级联延迟()
    {
        var specs = PclTweenPresets.PageEnter(delayMs: 50);

        // PCL 把 delay 逐条传进三个 Aa* 调用 —— 三条共享同一个值。
        Assert.All(specs, s => Assert.Equal(50, s.DelayMs));
    }

    [Fact]
    public void 级联步进是25毫秒()
    {
        Assert.Equal(25, PclTweenPresets.StaggerMs);

        var groups = PclTweenPresets.PageEnterStagger(3);

        Assert.Equal(3, groups.Count);
        Assert.Equal(0, groups[0][0].DelayMs);
        Assert.Equal(25, groups[1][0].DelayMs);
        Assert.Equal(50, groups[2][0].DelayMs);
    }

    [Fact]
    public void 滚动条入场从10滑回0()
    {
        var spec = PclTweenPresets.ScrollBarSlide();

        // PCL: scroll.RenderTransform = new TranslateTransform(10d, 0d);
        //      AaTranslateX(scroll, -X, 350, 0, AniEaseOutFluent())
        Assert.Equal(TweenChannel.TranslateX, spec.Channel);
        Assert.Equal(-10d, spec.Value);
        Assert.Equal(350, spec.DurationMs);
    }

    [Fact]
    public void 退场是70毫秒并向上收走6像素()
    {
        var specs = PclTweenPresets.PageExit();

        Assert.Equal(2, specs.Count);
        Assert.Equal(70, specs[0].DurationMs);
        Assert.Equal(-6d, specs[1].Value);
        Assert.Equal(TweenChannel.TranslateY, specs[1].Channel);
    }

    [Fact]
    public void 退场级联步进是15毫秒()
    {
        Assert.Equal(15, PclTweenPresets.ExitStaggerMs);
    }

    [Fact]
    public void 负数级联延迟被夹到零()
    {
        var specs = PclTweenPresets.PageEnter(delayMs: -100);
        Assert.All(specs, s => Assert.Equal(0, s.DelayMs));
    }

    [Fact]
    public void 级联子项数为负时报错()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PclTweenPresets.PageEnterStagger(-1));
    }

    [Fact]
    public void 预设的缓动是PCL那三档()
    {
        // 淡入 = AniEaseOutFluent(Weak) = 1-(1-t)^2
        Assert.IsType<FluentOutEasing>(PclTweenPresets.FadeInEase);
        Assert.Equal(2d, ((FluentOutEasing)PclTweenPresets.FadeInEase).Power);

        // 中段 = AniEaseOutFluent() = 1-(1-t)^3
        Assert.IsType<FluentOutEasing>(PclTweenPresets.EnterMidEase);
        Assert.Equal(3d, ((FluentOutEasing)PclTweenPresets.EnterMidEase).Power);

        // 末段 = AniEaseOutBack，指数随档位变化
        Assert.IsType<BackEaseWithPowerEasing>(PclTweenPresets.EnterToEase);
    }

    [Fact]
    public void 整组入场播完时Y回到0且不透明度为1()
    {
        // 这是"PCL 的入场"最核心的一条不变量：
        // 初值 -16，两段并行叠加 +5 与 +11，终态必须是 0。
        var opacity = new ValueAnimatable(0d);
        var y = new ValueAnimatable(PclTweenPresets.EnterFromY);

        var plan = TweenPlanner.Build(
            PclTweenPresets.PageEnter(),
            [
                new TweenChannelBinding(TweenChannel.Opacity, opacity),
                new TweenChannelBinding(TweenChannel.TranslateY, y),
            ],
            fps: 60);

        var frames = plan.Binding.Animation.DurationFrames;
        plan.Binding.Animation.ApplyFrame(frames, opacity);

        Assert.Equal(0d, Convert.ToDouble(y.GetValue()), 6);
        Assert.Equal(1d, Convert.ToDouble(opacity.GetValue()), 6);
    }

    [Fact]
    public void 入场中途位移是向上收敛而不是先冲下去()
    {
        // PCL 的观感：元素从上方 16px 处落下，中段略微过冲（-11 → -5），
        // 末段回弹到 0。所以中途的 Y 应当**大于**起始值（即已向下走了）。
        var y = new ValueAnimatable(PclTweenPresets.EnterFromY);
        var opacity = new ValueAnimatable(0d);

        var plan = TweenPlanner.Build(
            PclTweenPresets.PageEnter(),
            [
                new TweenChannelBinding(TweenChannel.Opacity, opacity),
                new TweenChannelBinding(TweenChannel.TranslateY, y),
            ],
            fps: 60);

        // 约 125ms 处（总时长 350ms 的三分之一）
        var frames = plan.Binding.Animation.DurationFrames;
        plan.Binding.Animation.ApplyFrame(Math.Max(1, frames / 3), y);

        var value = Convert.ToDouble(y.GetValue());
        Assert.True(value > PclTweenPresets.EnterFromY, $"中途位移 {value} 应大于起始值 {PclTweenPresets.EnterFromY}");
        Assert.True(value < 0d, $"中途位移 {value} 应尚未越过终点 0");
    }

    [Fact]
    public void 两条位移补间是并行的而不是先后()
    {
        // 并行与顺序的**可观测差异**是总时长：
        //   并行 → 总时长 = max(250, 350) = 350ms
        //   顺序 → 总时长 = 250 + 350 = 600ms
        // 以及"到 350ms 那一刻"的位置：
        //   并行 → 两段都已跑完，Y 恰好 = 0
        //   顺序 → 第二段才跑了 100/350，Y 明显不等于 0（会过冲）
        var y = new ValueAnimatable(PclTweenPresets.EnterFromY);
        var opacity = new ValueAnimatable(0d);

        var plan = TweenPlanner.Build(
            PclTweenPresets.PageEnter(),
            [
                new TweenChannelBinding(TweenChannel.Opacity, opacity),
                new TweenChannelBinding(TweenChannel.TranslateY, y),
            ],
            fps: 60);

        // 总时长必须是 350ms（= max 段长），不是 600ms（= 段长之和）。
        Assert.Equal(21, plan.Binding.Animation.DurationFrames);   // 350ms × 60fps

        // 350ms 时刻，Y 必须**恰好**归零（两段都跑完了）。
        plan.Binding.Animation.ApplyFrame(21, y);
        Assert.Equal(0d, Convert.ToDouble(y.GetValue()), 6);
    }
}
