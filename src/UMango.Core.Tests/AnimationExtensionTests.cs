using UMango.Core.Animation;
using UMango.Core.Animation.Clock;
using UMango.Core.Animation.Values;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 动画系统补齐（P2-2）的测试。
///
/// 覆盖 PCL-CE `PCL.Core/UI/Animation/` 迁过来的这几块能力：
/// 值处理器的算术运算、绝对/相对值动画、ActionAnimation、复合缓动、
/// 便利可动画对象、按名取消。
/// </summary>
public class AnimationExtensionTests
{
    // ---------- 值处理器的算术运算 ----------

    [Fact]
    public void 加减乘以容差比较都能算()
    {
        var p = new DoubleValueProcessor();

        Assert.Equal(3.0, (double)p.Add(1.0, 2.0)!, 6);
        Assert.Equal(-1.0, (double)p.Subtract(1.0, 2.0)!, 6);
        Assert.Equal(2.5, (double)p.Scale(1.0, 2.5)!, 6);
        Assert.Equal(0.0, (double)p.DefaultValue()!, 6);
    }

    [Fact]
    public void 相等比较用容差而不是精确相等()
    {
        var p = new DoubleValueProcessor();

        // 浮点累加的经典问题：0.1+0.2 != 0.3
        Assert.True(p.Equal(0.1 + 0.2, 0.3));
        Assert.False(p.Equal(1.0, 1.1));
    }

    [Fact]
    public void 数值处理器接受所有数值类型()
    {
        var p = new DoubleValueProcessor();

        Assert.True(p.CanProcess(typeof(int)));
        Assert.True(p.CanProcess(typeof(long)));
        Assert.True(p.CanProcess(typeof(float)));
        Assert.True(p.CanProcess(typeof(decimal)));
        Assert.False(p.CanProcess(typeof(string)));
    }

    [Fact]
    public void 注册表在找不到处理器时降级而不是崩()
    {
        var registry = new ValueProcessorRegistry();

        // string 没有处理器
        Assert.Null(registry.Add("a", "b"));
        Assert.Null(registry.Subtract("a", "b"));
        Assert.Equal("a", registry.Scale("a", 2));
        Assert.Null(registry.DefaultValue(typeof(string)));
        Assert.True(registry.Equal("a", "a"));   // 引用相等
        Assert.False(registry.Equal("a", "b"));
    }

    [Fact]
    public void 注册表的算术运算会走处理器()
    {
        var registry = new ValueProcessorRegistry();

        Assert.Equal(5.0, (double)registry.Add(2.0, 3.0)!, 6);
        Assert.Equal(1.0, (double)registry.Subtract(3.0, 2.0)!, 6);
        Assert.Equal(6.0, (double)registry.Scale(2.0, 3.0)!, 6);
        Assert.True(registry.Equal(1.0, 1.0 + 1e-9));
    }

    // ---------- 绝对值 / 相对值 ----------

    [Fact]
    public void 绝对值模式直接写目标值()
    {
        var target = new ValueAnimatable(0.0);
        var anim = new FromToAnimation(0.0, 10.0, 10);

        anim.ApplyFrame(5, target);

        Assert.Equal(5.0, (double)target.Value!, 6);
    }

    [Fact]
    public void 相对值模式把偏移加到起始值上()
    {
        var target = new ValueAnimatable(100.0);
        var anim = new FromToAnimation(0.0, 10.0, 10, valueType: AnimationValueType.Relative);

        anim.ApplyFrame(10, target);

        // 起始值 100 + 偏移 10 = 110
        Assert.Equal(110.0, (double)target.Value!, 6);
    }

    [Fact]
    public void 相对值模式不会逐帧累加()
    {
        var target = new ValueAnimatable(0.0);
        var anim = new FromToAnimation(0.0, 10.0, 10, valueType: AnimationValueType.Relative);

        // 逐帧推进：若每帧都拿"当前值 + 偏移"，结果会是 1,3,6,10…
        for (var f = 0; f <= 10; f++) anim.ApplyFrame(f, target);

        Assert.Equal(10.0, (double)target.Value!, 6);
    }

    [Fact]
    public void 相对值模式复位后能重新播放()
    {
        var target = new ValueAnimatable(100.0);
        var anim = new FromToAnimation(0.0, 10.0, 10, valueType: AnimationValueType.Relative);

        anim.ApplyFrame(10, target);
        Assert.Equal(110.0, (double)target.Value!, 6);

        // 复位后目标被改回 50，再播一次应得到 60（而不是 120）
        anim.Reset();
        target.Value = 50.0;
        anim.ApplyFrame(10, target);

        Assert.Equal(60.0, (double)target.Value!, 6);
    }

    [Fact]
    public void 相对值动画记录了起始值()
    {
        var target = new ValueAnimatable(7.0);
        var anim = new FromToAnimation(0.0, 1.0, 4, valueType: AnimationValueType.Relative);

        anim.ApplyFrame(0, target);

        Assert.Equal(7.0, (double)anim.RelativeStartValue!, 6);
    }

    // ---------- ActionAnimation ----------

    [Fact]
    public void 动作动画只执行一次回调()
    {
        var count = 0;
        var anim = new ActionAnimation(() => count++);

        anim.ApplyFrame(0, EmptyAnimatable.Instance);
        anim.ApplyFrame(1, EmptyAnimatable.Instance);
        anim.ApplyFrame(0, EmptyAnimatable.Instance);

        Assert.Equal(1, count);
        Assert.True(anim.WasInvoked);
    }

    [Fact]
    public void 动作动画是单帧的()
    {
        var anim = new ActionAnimation(() => { });
        Assert.Equal(1, anim.DurationFrames);
    }

    [Fact]
    public void 动作动画的异常被记录而不是吞掉()
    {
        var anim = new ActionAnimation(() => throw new InvalidOperationException("boom"));

        anim.ApplyFrame(0, EmptyAnimatable.Instance);

        Assert.NotNull(anim.AnimationFault);
        Assert.Equal("boom", anim.AnimationFault!.Message);
    }

    [Fact]
    public void 取消动作动画会取消令牌()
    {
        CancellationToken seen = default;
        var anim = new ActionAnimation(ct => seen = ct);

        // 先跑一帧，让回调拿到令牌
        anim.ApplyFrame(0, EmptyAnimatable.Instance);
        Assert.False(seen.IsCancellationRequested);

        anim.Cancel();

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(AnimationStatus.Canceled, anim.Status);
    }

    [Fact]
    public void 动作动画复位后能再执行一次()
    {
        var count = 0;
        var anim = new ActionAnimation(() => count++);

        anim.ApplyFrame(0, EmptyAnimatable.Instance);
        anim.Reset();
        anim.ApplyFrame(0, EmptyAnimatable.Instance);

        Assert.Equal(2, count);
    }

    // ---------- 复合缓动 ----------

    [Fact]
    public void 拼接缓动在分界点连续()
    {
        var e = new CombinedEasing(Easing.Of(EasingKind.QuadIn), Easing.Of(EasingKind.QuadOut), 0.5);

        // 分界点前后应当连续（不跳变）
        var before = e.Ease(0.4999);
        var at = e.Ease(0.5);
        Assert.True(Math.Abs(at - before) < 0.01, $"分界处不连续：{before} vs {at}");
    }

    [Fact]
    public void 拼接缓动的分界点被夹在开区间()
    {
        // split=0 会让第一段长度为 0（除零）—— PCL 用 0.00001~0.99999 夹住
        var e = new CombinedEasing(Easing.Of(EasingKind.Linear), Easing.Of(EasingKind.Linear), 0);

        Assert.True(e.Split > 0 && e.Split < 1);
        Assert.False(double.IsNaN(e.Ease(0.5)));
    }

    [Fact]
    public void 拼接缓动首尾分别是零和一()
    {
        var e = new CombinedEasing(Easing.Of(EasingKind.CubicIn), Easing.Of(EasingKind.CubicOut));

        Assert.Equal(0.0, e.Ease(0.0), 6);
        Assert.Equal(1.0, e.Ease(1.0), 6);
    }

    [Fact]
    public void 复合缓动按权重混合()
    {
        // 两段线性、各占一半权重：结果应等于整体线性
        var e = new CompositeEasing(
            (Easing.Of(EasingKind.Linear), TimeSpan.FromMilliseconds(100), TimeSpan.Zero, 0.5),
            (Easing.Of(EasingKind.Linear), TimeSpan.FromMilliseconds(100), TimeSpan.Zero, 0.5));

        Assert.Equal(0.5, e.Ease(0.5), 6);
    }

    [Fact]
    public void 复合缓动尊重延迟()
    {
        // 第一段有 50ms 延迟，20ms 时它还没开始 → 贡献 0
        var e = new CompositeEasing(
            (Easing.Of(EasingKind.Linear), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), 1.0));

        Assert.Equal(0.0, e.Ease(0.2), 6);
        Assert.True(e.Ease(0.75) > 0);
    }

    [Fact]
    public void 复合缓动拒绝空列表与非正时长()
    {
        // 空数组要显式给类型 —— 四个重载在零参数时无法由编译器推断
        Assert.Throws<ArgumentException>(() =>
            new CompositeEasing(Array.Empty<(Easing easing, TimeSpan duration)>()));
        Assert.Throws<ArgumentException>(() =>
            new CompositeEasing((Easing.Of(EasingKind.Linear), TimeSpan.Zero)));
    }

    [Fact]
    public void 复合缓动的总时长取各段最晚结束时间()
    {
        var e = new CompositeEasing(
            (Easing.Of(EasingKind.Linear), TimeSpan.FromMilliseconds(100), TimeSpan.Zero, 0.5),
            (Easing.Of(EasingKind.Linear), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), 0.5));

        Assert.Equal(TimeSpan.FromMilliseconds(150), e.TotalDuration);
    }

    [Fact]
    public void 回弹缓动的指数随强度变化()
    {
        var weak = new BackEaseWithPowerEasing(EasePower.Weak);
        var strong = new BackEaseWithPowerEasing(EasePower.Strong);

        // PCL 公式：p = 3.0 - power * 0.5
        // Weak=2 → 2.0；Middle=3 → 1.5；Strong=4 → 1.0；ExtraStrong=5 → 0.5
        Assert.Equal(2.0, weak.Power, 6);
        Assert.Equal(1.0, strong.Power, 6);
        Assert.Equal(1.5, new BackEaseWithPowerEasing(EasePower.Middle).Power, 6);
        Assert.Equal(0.5, new BackEaseWithPowerEasing(EasePower.ExtraStrong).Power, 6);
    }

    [Fact]
    public void 回弹缓动首尾是零和一()
    {
        foreach (var power in new[] { EasePower.Weak, EasePower.Middle, EasePower.Strong })
        {
            var e = new BackEaseWithPowerEasing(power);
            Assert.Equal(0.0, e.Ease(0.0), 6);
            Assert.Equal(1.0, e.Ease(1.0), 6);
        }
    }

    // ---------- 便利可动画对象 ----------

    [Fact]
    public void 空可动画对象读写都不出错()
    {
        Assert.Null(EmptyAnimatable.Instance.GetValue());
        EmptyAnimatable.Instance.SetValue(123);
    }

    [Fact]
    public void 带类型的可动画对象能读写所有者的属性()
    {
        var owner = new Box { Value = 5.0 };
        var animatable = new ClrAnimatable<Box, double>(owner, b => b.Value, (b, v) => b.Value = v);

        animatable.SetValue(9.0);

        Assert.Equal(9.0, owner.Value, 6);
        Assert.Equal(9.0, (double)animatable.GetValue()!, 6);
    }

    private sealed class Box
    {
        public double Value { get; set; }
    }

    // ---------- 按名取消 ----------

    [Fact]
    public void 按名取消能撤掉指定的动画()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock);

        var target = new ValueAnimatable(0.0);
        var anim = new FromToAnimation(0.0, 10.0, 100) { Name = "PageEnter" };

        _ = service.RunAsync(new AnimationBinding(anim, target));

        Assert.True(service.IsRunning("PageEnter"));

        var cancelled = service.CancelByName("PageEnter");

        Assert.Equal(1, cancelled);
        Assert.False(service.IsRunning("PageEnter"));
        Assert.Equal(AnimationStatus.Canceled, anim.Status);
    }

    [Fact]
    public void 按名取消不会误伤别的动画()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock);

        var a = new FromToAnimation(0.0, 1.0, 100) { Name = "A" };
        var b = new FromToAnimation(0.0, 1.0, 100) { Name = "B" };

        _ = service.RunAsync(new AnimationBinding(a, new ValueAnimatable(0.0)));
        _ = service.RunAsync(new AnimationBinding(b, new ValueAnimatable(0.0)));

        Assert.Equal(1, service.CancelByName("A"));
        Assert.True(service.IsRunning("B"));
    }

    [Fact]
    public void 取消不存在的名字不报错()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock);

        Assert.Equal(0, service.CancelByName("不存在的动画"));
        Assert.Equal(0, service.CancelByName(""));
        Assert.False(service.IsRunning(""));
    }
}
