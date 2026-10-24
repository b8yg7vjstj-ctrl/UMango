using System.Windows.Threading;
using UMango.Core.Animation.UIAccess;

namespace UMango.App.Animation;

/// <summary>
/// WPF 版的 <see cref="IUIAccessProvider"/>：把回调编组到 WPF 的 <see cref="Dispatcher"/>。
///
/// 对应 PCL-CE 的 <c>PCL.Core/UI/Animation/UIAccessProvider/WpfUIAccessProvider.cs</c>
/// （Apache-2.0）。
///
/// 关键点：<see cref="Dispatcher"/> 有两个容易用错的地方，这里都处理了：
/// ① <c>Dispatcher.Invoke</c> 在**非 UI 线程**调用时会**阻塞**等待 UI 线程处理完 ——
///    如果 UI 线程此刻正等着这个工作线程，就会死锁。
///    因此同步版本在「已经在 UI 线程」时直接执行，不走 Invoke。
/// ② 异步版本必须用 <c>BeginInvoke</c>（不是 <c>Invoke</c>）——
///    否则每个帧都要等 UI 线程往返一次，动画会被拖成幻灯片。
/// </summary>
public sealed class WpfUIAccessProvider : IUIAccessProvider
{
    private readonly Dispatcher _dispatcher;

    public WpfUIAccessProvider(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        // BeginInvoke + TaskCompletionSource：不等 UI 线程处理完就返回
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    public Task InvokeAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return func();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await func().ConfigureAwait(true);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return func();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                tcs.TrySetResult(await func().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    /// <summary>
    /// 帧事件：接 WPF 的 <see cref="System.Windows.Media.CompositionTarget.Rendering"/>。
    ///
    /// 它只在 UI 线程上触发 —— 这正是 <c>IUIClock</c> 标记接口要区分的情形：
    /// 宿主可以据此判断"这个时钟不需要额外编组"。
    /// </summary>
    public event EventHandler? FrameTick
    {
        add
        {
            if (value is null) return;
            System.Windows.Media.CompositionTarget.Rendering += OnRendering;
            _renderingHandlers += value;
        }
        remove
        {
            if (value is null) return;
            _renderingHandlers -= value;
            if (_renderingHandlers is null)
                System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        }
    }

    private EventHandler? _renderingHandlers;

    private void OnRendering(object? sender, EventArgs e)
    {
        _renderingHandlers?.Invoke(this, e);
    }
}
