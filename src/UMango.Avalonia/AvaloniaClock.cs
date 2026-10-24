using Avalonia.Threading;
using UMango.Core.Animation.Clock;
using UMango.Core.Animation.UIAccess;

namespace UMango.Avalonia;

/// <summary>
/// 基于 Avalonia <see cref="DispatcherTimer"/> 的帧时钟。
///
/// 与 WPF 侧的 <c>WpfClock</c>（<c>src/UMango.App/Animation/WpfClock.cs</c>）同构：
/// DispatcherTimer 天然只在 UI 线程上 Tick，因此**不需要**额外编组。
/// </summary>
public sealed class AvaloniaClock : IClock, IUIClock, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private long _frame;

    public AvaloniaClock(int fps = 60, Dispatcher? dispatcher = null)
    {
        _fps = Math.Max(1, fps);
        _dispatcher = dispatcher ?? Dispatcher.UIThread;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / _fps)
        };
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
