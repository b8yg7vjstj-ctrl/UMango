using UMango.Core.Animation;
using UMango.Core.Animation.Clock;
using UMango.Core.Animation.Values;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 帧驱动动画引擎的测试。
///
/// **这次移植最大的收益就是这里**：PCL 的动画引擎是自研帧驱动（不用 WPF 的
/// Storyboard/Timeline），因此它天然与渲染框架解耦 —— 配上
/// <see cref="ManualClock"/> 可以**逐帧精确断言**，而不是 sleep 之后"大概到了"。
/// </summary>
[Trait("Category", "接线")]
public class AnimationEngineTests
{
    // ================= 值处理器 =================

    [Fact]
    public void double插值按进度线性取值()
    {
        var p = new DoubleValueProcessor();
        Assert.Equal(0.0, p.Process(0.0, 100.0, 0.0));
        Assert.Equal(25.0, p.Process(0.0, 100.0, 0.25));
        Assert.Equal(100.0, p.Process(0.0, 100.0, 1.0));
    }

    [Fact]
    public void 插值支持反向与int()
    {
        var p = new DoubleValueProcessor();
        Assert.Equal(60.0, p.Process(100.0, 0.0, 0.4));      // 反向
        Assert.Equal(4.0, p.Process(2, 6, 0.5));             // int 输入
    }

    [Fact]
    public void 注册表能找到可赋值的处理器()
    {
        var registry = new ValueProcessorRegistry();
        Assert.NotNull(registry.Find(typeof(double)));
        Assert.NotNull(registry.Find(typeof(int)));            // int 可由 double 处理器处理
        Assert.Null(registry.Find(typeof(string)));            // 没有处理器 → null，而不是抛

        // 找不到处理器时返回 null，调用方跳过即可 —— 表现层的东西不该让流程崩掉
        Assert.Null(registry.Process("a", "b", 0.5));
    }

    // ================= 插值动画 =================

    [Fact]
    public void 无缓动时按帧线性推进且终帧精确到位()
    {
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 100.0, durationFrames: 10, Easing.Of(EasingKind.Linear));

        anim.ApplyFrame(0, target);
        Assert.Equal(0.0, target.Value);

        anim.ApplyFrame(5, target);
        Assert.Equal(50.0, target.Value);

        anim.ApplyFrame(10, target);
        Assert.Equal(100.0, target.Value);          // ★ 终帧必须精确等于终值
        Assert.Equal(AnimationStatus.Completed, anim.Status);
    }

    [Fact]
    public void 帧号越界被夹住而不是外推()
    {
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 100.0, 10);

        anim.ApplyFrame(999, target);
        Assert.Equal(100.0, target.Value);           // 不 extrapolate

        anim.ApplyFrame(-5, target);
        Assert.Equal(0.0, target.Value);
    }

    [Fact]
    public void 缓动生效且端点仍是精确值()
    {
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 100.0, 10, Easing.Of(EasingKind.CubicIn));

        anim.ApplyFrame(5, target);
        // CubicIn(0.5) = 0.125 → 12.5，明显低于线性的 50
        Assert.Equal(12.5, (double)target.Value!, 6);

        anim.ApplyFrame(10, target);
        Assert.Equal(100.0, target.Value);           // 端点由基类夹住，不受缓动影响
    }

    [Fact]
    public void 时长为零的动画落到终值()
    {
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(5.0, 42.0, 0);

        anim.ApplyFrame(0, target);
        Assert.Equal(42.0, target.Value);
    }

    // ================= 关键帧（本次新增能力）=================

    [Fact]
    public void 关键帧按段插值且段内进度被正确归一化()
    {
        var target = new ValueAnimatable();
        // 0 → 100（在 0.5 处到 100）→ 0（在 1.0 处回到 0）
        var anim = new FromToAnimation(0.0, 0.0, 10, Easing.Of(EasingKind.Linear),
            keyframes: [new AnimationKeyframe(0.5, 100.0)]);

        anim.ApplyFrame(5, target);   // 进度 0.5，正好在关键帧上
        Assert.Equal(100.0, (double)target.Value!, 6);

        // 段内归一化：第二段（0.5→1.0）的中点（进度 0.75）应走到 100→0 的一半 = 50
        // 若忘了归一化、直接用全局进度，这里会得到 100 + (0-100)*0.75 = 25（错）
        anim.ApplyFrame(7, target);
        var v = (double)target.Value!;
        Assert.True(v < 100 && v > 0, $"应落在 100→0 之间，实际 {v}");

        anim.ApplyFrame(10, target);
        Assert.Equal(0.0, (double)target.Value!, 6);   // 终帧回到 To
    }

    [Fact]
    public void 多关键帧按区间寻段()
    {
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 100.0, 100, Easing.Of(EasingKind.Linear),
            keyframes:
            [
                new AnimationKeyframe(0.25, 10.0),
                new AnimationKeyframe(0.50, 90.0),
                new AnimationKeyframe(0.75, 20.0),
            ]);

        anim.ApplyFrame(25, target);
        Assert.Equal(10.0, (double)target.Value!, 6);

        anim.ApplyFrame(50, target);
        Assert.Equal(90.0, (double)target.Value!, 6);

        anim.ApplyFrame(75, target);
        Assert.Equal(20.0, (double)target.Value!, 6);
    }

    // ================= 组合动画 =================

    [Fact]
    public void 顺序组的总时长是子动画之和()
    {
        var group = new SequentialAnimationGroup();
        group.Add(new FromToAnimation(0.0, 1.0, 10), new ValueAnimatable());
        group.Add(new FromToAnimation(0.0, 1.0, 20), new ValueAnimatable());

        Assert.Equal(30, group.DurationFrames);
    }

    [Fact]
    public void 顺序组先演完的子动画固定在终值()
    {
        var a = new ValueAnimatable();
        var b = new ValueAnimatable();
        var group = new SequentialAnimationGroup();
        group.Add(new FromToAnimation(0.0, 10.0, 10, Easing.Of(EasingKind.Linear)), a);
        group.Add(new FromToAnimation(0.0, 20.0, 10, Easing.Of(EasingKind.Linear)), b);

        group.ApplyFrame(15, new ValueAnimatable());   // 第一个已演完，第二个演到一半

        // 关键：先演完的必须固定在终值，不能停在中间（否则会留下"没到位"的视觉 bug）
        Assert.Equal(10.0, a.Value);
        Assert.Equal(10.0, b.Value);   // (15-10)/10 = 0.5 → 10
    }

    [Fact]
    public void 顺序组尚未轮到的不动但被初始化到起值()
    {
        var a = new ValueAnimatable();
        var b = new ValueAnimatable("初始");
        var group = new SequentialAnimationGroup();
        group.Add(new FromToAnimation(0.0, 10.0, 10), a);
        group.Add(new FromToAnimation(0.0, 20.0, 10), b);

        group.ApplyFrame(3, new ValueAnimatable());   // 还在第一个

        Assert.Equal(3.0, a.Value);
        Assert.Equal(0.0, b.Value);   // 被初始化到起值，而不是留在"初始"
    }

    [Fact]
    public void 并行组的总时长取最长者()
    {
        var group = new ParallelAnimationGroup();
        group.Add(new FromToAnimation(0.0, 1.0, 10), new ValueAnimatable());
        group.Add(new FromToAnimation(0.0, 1.0, 25), new ValueAnimatable());

        Assert.Equal(25, group.DurationFrames);
    }

    [Fact]
    public void 并行组里短的固定在终值长的继续()
    {
        var shortAnim = new ValueAnimatable();
        var longAnim = new ValueAnimatable();
        var group = new ParallelAnimationGroup();
        group.Add(new FromToAnimation(0.0, 10.0, 10, Easing.Of(EasingKind.Linear)), shortAnim);
        group.Add(new FromToAnimation(0.0, 100.0, 20, Easing.Of(EasingKind.Linear)), longAnim);

        group.ApplyFrame(15, new ValueAnimatable());

        Assert.Equal(10.0, shortAnim.Value);    // 已结束 → 终值
        Assert.Equal(75.0, longAnim.Value);     // 15/20 = 0.75 → 75
    }

    [Fact]
    public void 空组时长为零()
    {
        Assert.Equal(0, new ParallelAnimationGroup().DurationFrames);
        Assert.Equal(0, new SequentialAnimationGroup().DurationFrames);
    }

    // ================= 驱动器（手动时钟逐帧断言）=================

    [Fact]
    public async Task 驱动器按帧推进并在完成时返回()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());

        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 10.0, 5, Easing.Of(EasingKind.Linear));
        var task = service.RunAsync(new AnimationBinding(anim, target));

        clock.Start();
        // 这里只有**一个**绑定在跑（第一版写成了断言 5 —— 把 DurationFrames 当成了绑定数）。
        Assert.Single(service.Running);

        // 逐帧推进：帧号 0..DurationFrames，最后一帧到达终值并完成
        for (var i = 0; i <= 5; i++) clock.Advance();
        await task;

        Assert.Equal(10.0, target.Value);
        Assert.Empty(service.Running);          // 完成后自动移除
    }

    [Fact]
    public async Task 驱动器把每帧动作交给dispatch_线程编组集中在这一处()
    {
        var clock = new ManualClock();
        var dispatched = 0;
        using var service = new AnimationService(clock, action => { dispatched++; action(); });

        var anim = new FromToAnimation(0.0, 1.0, 3);
        var task = service.RunAsync(new AnimationBinding(anim, new ValueAnimatable()));

        // 帧号是 0..DurationFrames（含两端），所以要推进 DurationFrames+1 次。
        // 第一版只推进了 3 次（帧 0/1/2），动画永远到不了第 3 帧 —— 于是 await 永久挂起。
        clock.Advance(anim.DurationFrames + 1);
        await task;

        Assert.True(dispatched > 0, "每帧都应经过 dispatch —— 否则写 WPF 属性会遇到跨线程问题");
    }

    [Fact]
    public async Task 时长为零的动画立即完成且不占用帧()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());
        var target = new ValueAnimatable();

        await service.RunAsync(new AnimationBinding(new FromToAnimation(0.0, 7.0, 0), target));

        Assert.Equal(7.0, target.Value);        // 仍然写入了终值
        Assert.Empty(service.Running);
        Assert.False(clock.IsRunning);          // 没有动画在跑，时钟不该被启动
    }

    [Fact]
    public async Task 取消令牌能让动画提前结束()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());
        using var cts = new CancellationTokenSource();

        var task = service.RunAsync(
            new AnimationBinding(new FromToAnimation(0.0, 1.0, 100), new ValueAnimatable()),
            cts.Token);

        clock.Advance(3);
        cts.Cancel();
        await task;

        Assert.Empty(service.Running);
    }

    [Fact]
    public void 取消全部会清空运行列表()
    {
        var service = new AnimationService(new ManualClock(), action => action());
        service.RunAsync(new AnimationBinding(new FromToAnimation(0.0, 1.0, 10), new ValueAnimatable()));
        service.RunAsync(new AnimationBinding(new FromToAnimation(0.0, 1.0, 10), new ValueAnimatable()));
        Assert.Equal(2, service.Running.Count);

        service.CancelAll();

        Assert.Empty(service.Running);
    }

    [Fact]
    public void 没有动画在跑时时钟会停下_不空转唤醒线程()
    {
        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());
        clock.Start();

        clock.Advance();   // 空列表触发一次 Tick

        Assert.False(clock.IsRunning);
    }

    [Fact]
    public async Task 时钟无法启动时不得永久挂起_回归()
    {
        // 这是真实发生过的缺陷：ManualClock 靠手动推进，
        // 若调用方少推一帧，RunAsync 返回的 Task 就永远不完成 ——
        // 测试里表现为"无输出的超时"，真机上表现为"动画卡住、后续 await 全被拖死"。
        // 修法：RunAsync 启动时钟后若发现它并没有在跑，就立刻落到终态并完成。
        var clock = new NeverStartingClock();
        using var service = new AnimationService(clock, action => action());
        var target = new ValueAnimatable();
        var anim = new FromToAnimation(0.0, 9.0, 10);

        var task = service.RunAsync(new AnimationBinding(anim, target));

        // 必须在有限时间内完成（这里给 2 秒；永久挂起时该断言会因超时而失败）
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(task, finished);

        Assert.Equal(9.0, target.Value);      // 兜底路径也要写入终值
        Assert.Empty(service.Running);
    }

    /// <summary>Start 不生效的时钟（模拟"时钟启动失败/被暂停"）。</summary>
    private sealed class NeverStartingClock : IClock
    {
        public bool IsRunning => false;
        public int Fps { get; set; } = 60;
        public event EventHandler<long>? Tick;
        public void Start() { /* 故意不启动 */ }
        public void Stop() { }
        // 显式引用一下事件，避免编译器警告"从未使用"
        internal void Raise() => Tick?.Invoke(this, 0);
    }

    // ================= 与方向策略的桥接 =================

    [Fact]
    public async Task 方向策略算出的规格能直接驱动引擎_垂直矢量()
    {
        var settings = new AnimationSettings { Direction = AnimationDirectionMode.Mirrored };
        var spec = AnimationPlanner.Plan(ElementMotion.ListItem, settings);

        var x = new ValueAnimatable();
        var y = new ValueAnimatable();
        var opacity = new ValueAnimatable();
        var binding = AnimationPlannerBinding.CreateEntrance(spec, x, y, opacity, fps: 60);

        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());
        var task = service.RunAsync(binding);

        // 断言**中间帧**而不是终帧：动画演完后位移本就该归 0（元素回到自然位置），
        // 因此在终帧断言"有位移"是错的 —— 第一版就是这么写错的。
        clock.Advance(1);
        var midY = (double)y.Value!;
        var midX = (double)x.Value!;

        clock.Advance((int)binding.Animation.DurationFrames);
        await task;

        // 镜像模式：垂直有位移、水平没有（与"列表 = 垂直进入"的需求一致）
        Assert.NotEqual(0.0, midY);
        Assert.Equal(0.0, midX);
        Assert.Equal(0.0, y.Value);          // 终帧归位
        Assert.Equal(1.0, opacity.Value);
    }

    [Fact]
    public async Task 切到PCL矢量后水平方向才动()
    {
        var settings = new AnimationSettings { Direction = AnimationDirectionMode.PclCompatible };
        var spec = AnimationPlanner.Plan(ElementMotion.ListItem, settings);

        var x = new ValueAnimatable();
        var y = new ValueAnimatable();
        var binding = AnimationPlannerBinding.CreateEntrance(spec, x, y, new ValueAnimatable(), fps: 60);

        var clock = new ManualClock();
        using var service = new AnimationService(clock, action => action());
        var task = service.RunAsync(binding);

        clock.Advance(1);
        var midX = (double)x.Value!;
        var midY = (double)y.Value!;

        clock.Advance((int)binding.Animation.DurationFrames);
        await task;

        Assert.NotEqual(0.0, midX);          // 切到 PCL 矢量后水平方向才动
        Assert.Equal(0.0, midY);
        Assert.Equal(0.0, x.Value);
    }

    [Fact]
    public void 帧数换算按时长与帧率()
    {
        var spec = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings());
        var at60 = AnimationPlannerBinding.FramesFor(spec, 60);
        var at30 = AnimationPlannerBinding.FramesFor(spec, 30);

        Assert.True(at60 > at30);
        // 不能断言 at30 == at60/2：at60 是奇数时整除不成立（第一版就错在这里）。
        // 正确的断言是"帧率减半 → 帧数减半，允许 1 帧取整误差"。
        Assert.InRange(at30, at60 / 2 - 1, at60 / 2 + 1);

        // 关掉动画 → 零帧 → 瞬时完成
        var instant = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings { Enabled = false });
        Assert.Equal(0, AnimationPlannerBinding.FramesFor(instant, 60));
    }
}
