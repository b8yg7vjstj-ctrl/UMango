using System.Diagnostics;

namespace UMango.Core.Animation.Clock;

/// <summary>
/// 基于单调计时器的帧时钟。
///
/// **★ 与 PCL-CE 的一处刻意差异（重要）**：
/// PCL 的 `StopwatchClock` 用**忙等自旋**驱动（`while (!cancelled)` 循环体里没有等待）。
/// 在 59fps 下这会持续占满一个 CPU 核心 —— 对启动器或许能忍，对一个常驻的包管理界面不行。
/// 本实现改为**按帧间隔等待**（`Task.Delay` 到下一帧的应有时间），
/// 帧序号语义与 PCL 完全一致（Tick 携带从启动算起的帧序号）。
///
/// 帧序号由**单调**时间换算，因此不受系统时间调整影响（用 Stopwatch 而非 DateTime）。
/// </summary>
public sealed class StopwatchClock(int fps = 60) : IClock, IDisposable
{
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;
    private long _lastFrame = -1;

    public StopwatchClock() : this(60) { }

    public event EventHandler<long>? Tick;

    private int _fps = fps;

    public int Fps
    {
        get => _fps;
        set => _fps = Math.Max(1, value);
    }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _watch.Restart();
        _lastFrame = -1;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var frame = FrameOf(_watch.Elapsed);

                if (frame != _lastFrame)
                {
                    _lastFrame = frame;
                    try { Tick?.Invoke(this, frame); }
                    catch (Exception)
                    {
                        // 单个订阅者抛异常不该让时钟停摆：其它动画还等着这一帧。
                        // （异常已由订阅者自己记录，这里只保证时钟继续走。）
                    }
                }

                // 等到下一帧的应有时刻再检查，而不是原地自旋。
                // 上限 50ms 是为了让 Stop() 能及时生效。
                var nextFrameStart = TimeSpan.FromSeconds((frame + 1) / (double)Fps);
                var wait = nextFrameStart - _watch.Elapsed;
                var ms = wait > TimeSpan.Zero ? Math.Min(50, Math.Max(1, (int)wait.TotalMilliseconds)) : 1;
                try { await Task.Delay(ms, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _watch.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>把已运行时间换算成帧序号。</summary>
    private long FrameOf(TimeSpan elapsed)
        => Fps == int.MaxValue ? (long)(elapsed.TotalMilliseconds * 1000) : (long)(elapsed.TotalSeconds * Fps);
}
