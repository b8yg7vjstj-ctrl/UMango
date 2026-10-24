// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/Utils/Threading/AsyncCountResetEvent.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.Utils.Threading → UMango.Core.Threading。
//   2. 去掉终结器（~AsyncCountResetEvent）。原版在 Dispose 里调
//      GC.SuppressFinalize 却没有析构里的 base 处置，析构器在终结队列上
//      跑 Dispose 会阻塞终结线程 —— 而这里托管的资源只有锁和一个队列，
//      没有非托管资源需要释放，析构器纯属负担。
//   3. 增加 `Pending` 只读属性（原版没有），便于观测"有多少等待者"。
//   4. 修掉原版的一个真实缺陷：`Set` 里先唤醒等待者、剩余配额累加到
//      `_permits`，但**没有上限**。如果 Set 的次数远大于等待者数量，
//      _permits 会无限增长，之后的 WaitAsync 全部立即通过 —— 相当于
//      信号被"攒"起来了。UMango 加上 `MaxPermits` 上限（默认无上限时行为一致）。

using System.Threading;

namespace UMango.Core.Threading;

/// <summary>
/// 带配额的自动复位事件变体：一次可以释放多个等待者。
///
/// 与 <see cref="SemaphoreSlim"/> 的区别：语义更接近 AutoResetEvent ——
/// 没有"谁拿了哪个许可"的对应关系，Set 之后许可不会记在某个持有者账上。
/// </summary>
public sealed class AsyncCountResetEvent : IDisposable
{
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private readonly object _lock = new();
    private int _permits;
    private bool _disposed;

    /// <summary>累计可用的许可上限。0 表示不限（与原版一致）。</summary>
    public int MaxPermits { get; }

    public AsyncCountResetEvent(int maxPermits = 0) => MaxPermits = maxPermits;

    /// <summary>当前剩余的许可数。</summary>
    public int Permits
    {
        get { lock (_lock) return _permits; }
    }

    /// <summary>当前在等待的等待者数量。</summary>
    public int Pending
    {
        get { lock (_lock) return _waiters.Count; }
    }

    /// <summary>
    /// 等待一个信号。信号可用时立即返回已完成的 Task，否则排队等待。
    /// </summary>
    /// <exception cref="ObjectDisposedException">对象已被释放</exception>
    public Task WaitAsync()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(AsyncCountResetEvent));

            if (_permits > 0)
            {
                _permits--;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
            return tcs.Task;
        }
    }

    /// <summary>
    /// 释放一个或多个信号，让等待者继续执行。
    /// 没有等待者时，信号会累积成许可（供后续的 WaitAsync 立即通过）。
    /// </summary>
    public void Set(int count = 1)
    {
        if (count <= 0) return;

        lock (_lock)
        {
            if (_disposed) return;

            while (count > 0 && _waiters.Count > 0)
            {
                var tcs = _waiters.Dequeue();
                tcs.TrySetResult(true);
                count--;
            }

            if (count > 0)
            {
                _permits += count;
                if (MaxPermits > 0 && _permits > MaxPermits) _permits = MaxPermits;
            }
        }
    }

    /// <summary>释放对象：所有等待中的任务以异常结束。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            while (_waiters.Count > 0)
            {
                var tcs = _waiters.Dequeue();
                tcs.TrySetException(new ObjectDisposedException(nameof(AsyncCountResetEvent)));
            }
        }
    }
}
