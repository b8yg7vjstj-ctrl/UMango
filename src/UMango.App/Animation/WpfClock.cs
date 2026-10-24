using System.Windows.Threading;
using UMango.Core.Animation.Clock;

namespace UMango.App.Animation;

/// <summary>
/// 基于 WPF DispatcherTimer 的帧时钟。
///
/// 为什么要这个：Core 的 AnimationService 是平台中立的，它通过 IClock 接口驱动帧。
/// 但 WPF 的动画必须在 UI 线程上修改属性，所以帧回调必须跑在 Dispatcher 上。
/// DispatcherTimer 天然满足这一点 —— 它在 UI 线程上 Tick，不需要额外编组。
///
/// 与 Core 的 StopwatchClock 对比：
/// - StopwatchClock：在后台线程上 Tick，通过 AnimationService 的 dispatch 编组回 UI 线程
/// - WpfClock：直接在 UI 线程上 Tick，不需要编组
/// 两者产生相同的帧序号，动画引擎不关心时钟的实现细节。
/// </summary>
/// <remarks>
/// 实现 <see cref="IUIClock"/>：DispatcherTimer 天然只在 UI 线程上 Tick，
/// 因此宿主可以据此判断"这个时钟不需要额外编组"。
/// </remarks>
public sealed class WpfClock : IClock, IUIClock, IDisposable
{
    private readonly DispatcherTimer _timer;
    private long _frame;

    public WpfClock(int fps = 60)
    {
        _fps = Math.Max(1, fps);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / _fps) };
        _timer.Tick += OnTick;
    }

    private int _fps;

    public int Fps
    {
        get => _fps;
        set
        {
            _fps = Math.Max(1, value);
            _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        }
    }

    public bool IsRunning => _timer.IsEnabled;

    public event EventHandler<long>? Tick;

    public void Start()
    {
        if (IsRunning) return;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _frame = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _frame++;
        Tick?.Invoke(this, _frame);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
