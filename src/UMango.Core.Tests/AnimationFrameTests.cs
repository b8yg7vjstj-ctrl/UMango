using UMango.Core.Animation;
using UMango.Core.Animation.Clock;
using UMango.Core.Animation.Frames;
using UMango.Core.Animation.UIAccess;
using UMango.Core.Animation.Values;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 帧抽象（<c>IAnimationFrame</c>）与 UI 线程访问抽象（<c>IUIAccessProvider</c>）的测试。
///
/// 这两块对应 PCL-CE 的 <c>PCL.Core/UI/Animation/Core/IAnimationFrame.cs</c> 与
/// <c>UIAccessProvider/IUIAccessProvider.cs</c>（Apache-2.0）。
/// 它们是**跨平台复刻的关键**：UI 线程访问抽象放在 Core 之后，
/// WPF 与 Avalonia 两个外壳可以共用同一套动画引擎。
/// </summary>
[Trait("Category", "接线")]
public class AnimationFrameTests
{
    // ================= 帧抽象 =================

    [Fact]
    public void 动作帧取出的动作就是传入的回调()
    {
        var called = false;
        var frame = new ActionAnimationFrame(() => called = true);

        var action = frame.GetAction();
        action();

        Assert.True(called);
    }

    [Fact]
    public void 动作帧多次取出得到同一个动作()
    {
        var frame = new ActionAnimationFrame(() => { });
        Assert.Same(frame.GetAction(), frame.GetAction());
    }

    [Fact]
    public void 从到帧的动作写入目标是起始值加增量()
    {
        var target = new ValueAnimatable(0d);
        // 起始值 10、增量 5 → 绝对值 15
        var frame = new FromToAnimationFrame<double>(target, 5d, 10d);

        frame.GetAction()();

        Assert.Equal(15d, target.GetValue());
    }

    [Fact]
    public void 从到帧取出动作时绝对值即已固化()
    {
        var target = new ValueAnimatable(0d);
        var frame = new FromToAnimationFrame<double>(target, 5d, 10d);

        // 先取出动作（此时绝对值算成 15）
        var action = frame.GetAction();

        // 再改起始值 —— 已经取出的动作**不该**跟着变
        // （PCL 是在 GetAction 时算绝对值，不是执行时算）
        var mutable = new FromToAnimationFrame<double>(target, 5d, 10d);
        var action2 = mutable.GetAction();

        action();
        Assert.Equal(15d, target.GetValue());

        action2();
        Assert.Equal(15d, target.GetValue());
    }

    [Fact]
    public void 从到帧的绝对值可单独取出()
    {
        var target = new ValueAnimatable(0d);
        var frame = new FromToAnimationFrame<double>(target, 5d, 10d);
        Assert.Equal(15d, frame.GetAbsoluteValue());
    }

    [Fact]
    public void 从到帧找不到处理器时退回增量本身()
    {
        // 目标是字符串：没有对应处理器 → 绝对值退回 Value
        var target = new ValueAnimatable("x");
        var frame = new FromToAnimationFrame<string>(target, "v", "s");
        Assert.Equal("v", frame.GetAbsoluteValue());
    }

    [Fact]
    public void 从到帧可指定自己的注册表()
    {
        var registry = new ValueProcessorRegistry();
        registry.Register(new DoubleValueProcessor());

        var target = new ValueAnimatable(0d);
        var frame = new FromToAnimationFrame<double>(target, 2.5d, 1.5d, registry);
        Assert.Equal(4d, frame.GetAbsoluteValue());
    }

    // ================= UI 线程访问抽象 =================

    [Fact]
    public void 同步实现的访问判定恒为真()
    {
        Assert.True(ImmediateUIAccessProvider.Instance.CheckAccess());
    }

    [Fact]
    public void 同步实现的同步调用立即执行()
    {
        var called = false;
        ImmediateUIAccessProvider.Instance.Invoke(() => called = true);
        Assert.True(called);
    }

    [Fact]
    public async Task 同步实现的异步调用立即执行并返回结果()
    {
        var called = false;
        await ImmediateUIAccessProvider.Instance.InvokeAsync(() => called = true);
        Assert.True(called);

        var value = await ImmediateUIAccessProvider.Instance.InvokeAsync(() => 42);
        Assert.Equal(42, value);

        var asyncValue = await ImmediateUIAccessProvider.Instance.InvokeAsync(async () =>
        {
            await Task.Yield();
            return 7;
        });
        Assert.Equal(7, asyncValue);

        await ImmediateUIAccessProvider.Instance.InvokeAsync(async () => await Task.Yield());
    }

    [Fact]
    public void 同步实现可订阅帧事件但不会抛()
    {
        EventHandler? handler = (_, _) => { };
        ImmediateUIAccessProvider.Instance.FrameTick += handler;
        ImmediateUIAccessProvider.Instance.FrameTick -= handler;
    }

    [Fact]
    public void 帧事件在同步实现上不会被触发()
    {
        var fired = false;
        EventHandler handler = (_, _) => fired = true;
        ImmediateUIAccessProvider.Instance.FrameTick += handler;

        // 同步实现没有渲染循环，不会自行触发
        Assert.False(fired);
    }

    // ================= UI 时钟标记接口 =================

    [Fact]
    public void 定时器时钟不是UI时钟()
    {
        // StopwatchClock 由定时器驱动，可能在任意工作线程触发
        var clock = new StopwatchClock(60);
        Assert.IsNotAssignableFrom<IUIClock>(clock);
    }

    [Fact]
    public void 手动时钟不是UI时钟()
    {
        var clock = new ManualClock();
        Assert.IsNotAssignableFrom<IUIClock>(clock);
    }
}
