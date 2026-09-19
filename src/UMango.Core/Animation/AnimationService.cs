using UMango.Core.Animation.Clock;

namespace UMango.Core.Animation;

/// <summary>
/// 动画驱动器：持有帧时钟，按帧驱动所有在跑的动画。
///
/// 与 PCL-CE 的 `Core/AnimationService.cs` 对应（Apache-2.0）。
///
/// **线程编组集中在这里**：时钟可能在工作线程上触发（定时器时钟），
/// 也可能在 UI 线程上（渲染驱动时钟），而写 WPF 属性必须在 UI 线程上。
/// 因此服务接受一个 <c>dispatcher</c>，把每帧的求值动作统一投递过去 ——
/// 而不是让每个动画自己操心线程（那样必然有漏网的）。
/// </summary>
public sealed class AnimationService : IDisposable
{
    private readonly List<AnimationBinding> _running = [];
    private readonly Dictionary<AnimationBinding, TaskCompletionSource<bool>> _completions = [];
    private readonly Lock _gate = new();
    private readonly Action<Action> _dispatch;

    public AnimationService(IClock? clock = null, Action<Action>? dispatcher = null)
    {
        Clock = clock ?? new StopwatchClock(60);
        _dispatch = dispatcher ?? (action => action());
        Clock.Tick += OnTick;
    }

    public IClock Clock { get; }

    /// <summary>正在运行的绑定快照。</summary>
    public IReadOnlyList<AnimationBinding> Running
    {
        get { lock (_gate) return _running.ToList(); }
    }

    /// <summary>某个动画完成时触发（参数为该绑定）。</summary>
    public event Action<AnimationBinding>? AnimationCompleted;

    /// <summary>
    /// 运行一个动画并等待完成。
    /// 时长为 0 的动画会**立即**落到终帧并完成，不占用任何帧。
    /// </summary>
    public Task RunAsync(AnimationBinding binding, CancellationToken ct = default)
    {
        if (binding.Animation.DurationFrames <= 0)
        {
            // 瞬时：直接落到终态。走一遍 ApplyFrame 而不是什么都不做，
            // 保证目标确实被写入了终值。
            binding.Animation.ApplyFrame(0, binding.Target);
            binding.Animation.ApplyFrame(binding.Animation.DurationFrames, binding.Target);
            AnimationCompleted?.Invoke(binding);
            return Task.CompletedTask;
        }

        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            _running.Add(binding);
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completions[binding] = tcs;
        }

        if (!Clock.IsRunning) Clock.Start();

        // ★ 兜底：若时钟**没有真正跑起来**（实现的 Start 没生效、或时钟已被外部停掉），
        // 必须立刻落到终态并完成 —— 否则 RunAsync 会永久挂起。
        //
        // 这不是假想问题：本项目的 ManualClock 靠调用方手动推进，
        // 测试里"少推一帧"就会让 await task 永远等下去（真实发生过，表现为测试超时）。
        // 真机上对应的场景是"时钟驱动的渲染循环被暂停/窗口被隐藏"，
        // 那同样会让动画任务永不返回，并把它后面的 await 全部卡死。
        if (!Clock.IsRunning)
        {
            binding.Animation.ApplyFrame(binding.Animation.DurationFrames, binding.Target);
            Complete(binding);
            return tcs.Task;
        }

        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                binding.Animation.Cancel();
                Complete(binding);
            });
        }

        return tcs.Task;
    }

    /// <summary>一发即忘地运行（不关心完成时刻）。</summary>
    public void RunFireAndForget(AnimationBinding binding) => _ = RunAsync(binding);

    /// <summary>取消全部正在运行的动画。</summary>
    public void CancelAll()
    {
        List<AnimationBinding> snapshot;
        lock (_gate) snapshot = _running.ToList();

        foreach (var binding in snapshot)
        {
            binding.Animation.Cancel();
            Complete(binding);
        }
    }

    private void OnTick(object? sender, long frame)
    {
        List<AnimationBinding> snapshot;
        lock (_gate) snapshot = _running.ToList();

        if (snapshot.Count == 0)
        {
            // 没有动画在跑就停掉时钟：不必为一个空闲的界面持续唤醒线程
            Clock.Stop();
            return;
        }

        // 整帧统一编组：一次投递驱动全部动画，避免每帧投递 N 次
        _dispatch(() =>
        {
            foreach (var binding in snapshot)
            {
                if (binding.Animation.Status == AnimationStatus.Canceled) continue;

                binding.Animation.ApplyFrame((int)frame, binding.Target);

                if (binding.Animation.Status == AnimationStatus.Completed) Complete(binding);
            }
        });
    }

    private void Complete(AnimationBinding binding)
    {
        TaskCompletionSource<bool>? tcs = null;
        lock (_gate)
        {
            _running.Remove(binding);
            if (_completions.Remove(binding, out var found)) tcs = found;
        }

        tcs?.TrySetResult(true);
        AnimationCompleted?.Invoke(binding);
    }

    public void Dispose()
    {
        Clock.Tick -= OnTick;
        Clock.Stop();
        CancelAll();
    }
}
