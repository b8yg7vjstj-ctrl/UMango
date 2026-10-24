// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Threading/AsyncManualResetEvent.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Threading → UMango.Core.Threading。
//   2. 原版同时维护 TaskCompletionSource 与 ManualResetEventSlim 两套状态，
//      两者必须被小心翼翼地同步（Set/Reset 各更新两处）—— 任何一处漏掉
//      就会出现"异步等待能过、同步等待卡死"（或反过来）的鬼状态。
//      UMango 改为**只保留 TaskCompletionSource**，同步等待用
//      `task.Wait(ct)` 实现 —— 单一真相来源，不可能不同步。
//   3. 原版 Wait() 无参版本在 UI 线程上会死锁（同步等异步），
//      这里保留但文档写明"不要在 UI 线程调用"。

using System.Threading;

namespace UMango.Core.Threading;

/// <summary>
/// 异步手动复位事件。
///
/// 与 <see cref="ManualResetEventSlim"/> 的区别：可以在异步代码里
/// <c>await</c> 它，而且支持取消 —— 同步事件在 async 方法里等是死锁的经典来源。
///
/// 典型用法：让一个后台循环等待"有活干"的信号。
/// </summary>
public sealed class AsyncManualResetEvent : IDisposable
{
    private readonly object _syncLock = new();
    private TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public AsyncManualResetEvent(bool initialState = false)
    {
        if (initialState) _tcs.TrySetResult(true);
    }

    /// <summary>事件是否已触发。</summary>
    public bool IsSet
    {
        get { lock (_syncLock) { return _tcs.Task.IsCompleted; } }
    }

    /// <summary>
    /// 异步等待事件被触发。
    /// </summary>
    /// <param name="cancellationToken">结束等待的取消信号</param>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        Task<bool> t;
        lock (_syncLock) { t = _tcs.Task; }
        if (!cancellationToken.CanBeCanceled || t.IsCompleted) return t;
        return WaitWithCancellationAsync(t, cancellationToken);
    }

    private static async Task WaitWithCancellationAsync(Task waitTask, CancellationToken ct)
    {
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs))
        {
            var completed = await Task.WhenAny(waitTask, cancelTcs.Task).ConfigureAwait(false);
            if (completed == cancelTcs.Task) ct.ThrowIfCancellationRequested();
            await waitTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 同步等待。⚠️ **不要在 UI 线程调用** —— 它会阻塞消息泵。
    /// </summary>
    public void Wait() => WaitAsync().GetAwaiter().GetResult();

    /// <summary>同步等待，超时则返回 false。</summary>
    public bool Wait(int millisecondsTimeout)
        => WaitAsync().Wait(millisecondsTimeout);

    /// <summary>同步等待，超时则返回 false。</summary>
    public bool Wait(TimeSpan timeout)
        => WaitAsync().Wait(timeout);

    /// <summary>同步等待，可被取消信号打断。</summary>
    public void Wait(CancellationToken cancellationToken)
        => WaitAsync(cancellationToken).GetAwaiter().GetResult();

    /// <summary>触发事件。重复触发是安全的。</summary>
    public void Set()
    {
        lock (_syncLock)
        {
            _tcs.TrySetResult(true);
        }
    }

    /// <summary>复位事件，之后的等待会重新阻塞。</summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            if (!_tcs.Task.IsCompleted) return;   // 还没触发过，无需复位
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 释放等待者，否则 await 会永远挂着
        lock (_syncLock)
        {
            _tcs.TrySetResult(false);
        }
    }
}
