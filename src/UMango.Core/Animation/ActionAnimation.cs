namespace UMango.Core.Animation;

/// <summary>
/// 把一个回调当作动画来播：在动画的某一帧执行一次 <see cref="Action"/>。
///
/// 与 PCL-CE 的 `Core/ActionAnimation.cs` + `Core/ActionAnimationFrame.cs` 对应（Apache-2.0）。
///
/// 用途：让"一次性动作"能和别的动画**同步**播放。
/// 典型场景是 PCL 的加载动画 —— 背景转圈的同时做某件事，
/// 事情做完了整个动画组才结束。用普通 <c>Task</c> 做不到这点：
/// 任务完成与动画完成是两件事。
///
/// 语义：
/// · 动画只有**一帧**（<see cref="DurationFrames"/> 恒为 1），回调在那一帧执行一次；
/// · 回调可被取消（<see cref="Cancel"/> 会取消 <see cref="CancellationToken"/>）；
/// · 回调抛异常时，异常**不吞掉** —— 记到 <see cref="AnimationFault"/> 上，
///   由调用方决定怎么处理（动画框架不该替业务决定"出错了就崩"）。
/// </summary>
public sealed class ActionAnimation : AnimationBase
{
    private readonly Action<CancellationToken> _action;
    private readonly CancellationTokenSource _cts = new();
    private bool _invoked;

    public ActionAnimation(Action<CancellationToken> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
    }

    public ActionAnimation(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = _ => action();
    }

    /// <summary>回调抛出的异常（若有）。</summary>
    public Exception? AnimationFault { get; private set; }

    /// <summary>回调是否已被执行过（用于断言"只执行一次"）。</summary>
    public bool WasInvoked => _invoked;

    /// <summary>ActionAnimation 恒为单帧。</summary>
    public override int DurationFrames => 1;

    /// <summary>动画开始前的延迟（毫秒）。</summary>
    public int DelayMs { get; set; }

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        // 只执行一次：帧号可能被驱动多次（时钟对齐、边界兜底），
        // 回调被执行两次会造成重复副作用。
        if (_invoked) return;
        _invoked = true;

        try
        {
            _action(_cts.Token);
        }
        catch (Exception ex)
        {
            AnimationFault = ex;
        }
    }

    public override void Reset()
    {
        base.Reset();
        _invoked = false;
        AnimationFault = null;
    }

    public override void Cancel()
    {
        base.Cancel();

        // 取消令牌：回调若在等待某个异步操作，可以据此提前退出。
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }
}

/// <summary>
/// 延迟执行的一次性动作。
///
/// 与 PCL-CE 的 <c>Plain Craft Launcher 2/Modules/Base/ModAnimation.cs</c> 里
/// <c>AaCode(action, delay)</c> 的语义对应（受限许可目录，这里只借鉴设计语义）：
/// 动作在动画开始后的第 <c>delay</c> 毫秒处执行一次。
///
/// 与 <see cref="ActionAnimation"/> 的区别：
///   · <see cref="ActionAnimation"/> 恒为 1 帧（立即执行）；
///   · 本类有 <c>DelayFrames + 1</c> 帧，延迟到达才执行。
///
/// 这样"延迟动作"可以**放进动画组**里，与其它动画共享同一条时序 ——
/// 而不是靠一个独立的计时器（那会让它和动画组脱节）。
/// </summary>
public sealed class DelayedActionAnimation : AnimationBase
{
    private readonly Action _action;
    private bool _invoked;

    public DelayedActionAnimation(Action action, int delayFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
        DelayFrames = Math.Max(0, delayFrames);
    }

    /// <summary>延迟的帧数。</summary>
    public int DelayFrames { get; }

    /// <summary>回调是否已被执行过。</summary>
    public bool WasInvoked => _invoked;

    /// <summary>回调抛出的异常（若有）。</summary>
    public Exception? AnimationFault { get; private set; }

    /// <summary>总帧数 = 延迟帧数 + 1（最后一帧执行动作）。</summary>
    public override int DurationFrames => DelayFrames + 1;

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        if (_invoked) return;
        if (frame < DelayFrames) return;

        _invoked = true;
        try
        {
            _action();
        }
        catch (Exception ex)
        {
            AnimationFault = ex;
        }
    }

    public override void Reset()
    {
        base.Reset();
        _invoked = false;
        AnimationFault = null;
    }
}
