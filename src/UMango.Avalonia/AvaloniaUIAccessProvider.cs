using Avalonia.Threading;
using UMango.Core.Animation.UIAccess;

namespace UMango.Avalonia;

/// <summary>
/// Avalonia 版的 <see cref="IUIAccessProvider"/>：把回调编组到 Avalonia 的 UI 线程。
///
/// 与 WPF 侧的 <c>WpfUIAccessProvider</c>（<c>src/UMango.App/Animation/</c>）同构。
/// **这正是把 UI 线程访问抽象放进 Core 的意义**：
/// 同一套 Core 动画引擎，WPF 壳与 Avalonia 壳各自提供一份实现，
/// 引擎本身对平台一无所知。
///
/// 与 WPF 版的两个共同要点：
/// ① 同步版本在「已经在 UI 线程」时直接执行，不走 Invoke ——
///    避免 UI 线程等工作线程、工作线程等 UI 线程的死锁。
/// ② 异步版本用 <c>Post</c>（相当于 WPF 的 BeginInvoke），不等 UI 线程往返。
/// </summary>
public sealed class AvaloniaUIAccessProvider : IUIAccessProvider
{
    private readonly Dispatcher _dispatcher;

    public AvaloniaUIAccessProvider(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.UIThread;
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

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
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
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return func();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(async () =>
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
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        if (_dispatcher.CheckAccess())
            return func();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(async () =>
        {
            try
            {
                tcs.TrySetResult(await func().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>
    /// 帧事件：Avalonia 没有与 WPF <c>CompositionTarget.Rendering</c> 完全对应的公开 API，
    /// 因此这里**不**自动接线（保持空实现）——
    /// Avalonia 侧用渲染驱动时钟时，由该时钟自己提供 Tick 即可。
    /// </summary>
    public event EventHandler? FrameTick
    {
        add { }
        remove { }
    }
}
