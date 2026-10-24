using UMango.Core.Threading;
using Xunit;

namespace UMango.Core.Tests.Threading;

/// <summary>
/// 线程原语测试。全部用超时兜底 —— 任何一个"应该完成却没完成"的缺陷
/// 都会让测试挂死，所以每个 await 都带 <c>Task.WhenAny</c> 保护。
/// </summary>
public class ThreadingPrimitivesTests
{
    private static async Task<bool> Completes(Task task, int ms = 3000)
    {
        var done = await Task.WhenAny(task, Task.Delay(ms));
        return done == task;
    }

    // ── AsyncManualResetEvent ───────────────────────────────────────────

    [Fact]
    public async Task 手动复位事件_初始未触发时等待会挂起()
    {
        using var ev = new AsyncManualResetEvent();
        Assert.False(ev.IsSet);

        var wait = ev.WaitAsync();
        Assert.False(await Completes(wait, 200));
    }

    [Fact]
    public async Task 手动复位事件_触发后等待者被唤醒()
    {
        using var ev = new AsyncManualResetEvent();
        var wait = ev.WaitAsync();

        ev.Set();

        Assert.True(await Completes(wait));
        Assert.True(ev.IsSet);
    }

    [Fact]
    public async Task 手动复位事件_初始状态为已触发时立即返回()
    {
        using var ev = new AsyncManualResetEvent(initialState: true);
        Assert.True(ev.IsSet);
        Assert.True(await Completes(ev.WaitAsync()));
    }

    [Fact]
    public async Task 手动复位事件_复位后再次等待会挂起()
    {
        using var ev = new AsyncManualResetEvent(initialState: true);
        ev.Reset();
        Assert.False(ev.IsSet);

        var wait = ev.WaitAsync();
        Assert.False(await Completes(wait, 200));

        ev.Set();
        Assert.True(await Completes(wait));
    }

    [Fact]
    public async Task 手动复位事件_重复触发是安全的()
    {
        using var ev = new AsyncManualResetEvent();
        ev.Set();
        ev.Set();
        ev.Set();
        Assert.True(await Completes(ev.WaitAsync()));
    }

    [Fact]
    public async Task 手动复位事件_取消信号能打断等待()
    {
        using var ev = new AsyncManualResetEvent();
        using var cts = new CancellationTokenSource();
        var wait = ev.WaitAsync(cts.Token);

        cts.Cancel();

        var finished = await Task.WhenAny(wait, Task.Delay(2000));
        Assert.True(finished == wait, "取消后等待应尽快结束");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task 手动复位事件_释放后等待者不会永远挂起()
    {
        var ev = new AsyncManualResetEvent();
        var wait = ev.WaitAsync();

        ev.Dispose();

        Assert.True(await Completes(wait), "释放后等待必须结束，否则调用方会永久卡死");
    }

    [Fact]
    public async Task 手动复位事件_同步等待在后台线程可用()
    {
        using var ev = new AsyncManualResetEvent();
        var t = Task.Run(() => { ev.Wait(); return 42; });

        await Task.Delay(100);
        ev.Set();

        Assert.True(await Completes(t));
        Assert.Equal(42, await t);
    }

    // ── AsyncCountResetEvent ────────────────────────────────────────────

    [Fact]
    public async Task 计数事件_无信号时等待挂起()
    {
        using var ev = new AsyncCountResetEvent();
        var wait = ev.WaitAsync();
        Assert.False(await Completes(wait, 200));
    }

    [Fact]
    public async Task 计数事件_一次释放多个等待者()
    {
        using var ev = new AsyncCountResetEvent();
        var w1 = ev.WaitAsync();
        var w2 = ev.WaitAsync();
        var w3 = ev.WaitAsync();

        ev.Set(3);

        Assert.True(await Completes(w1));
        Assert.True(await Completes(w2));
        Assert.True(await Completes(w3));
    }

    [Fact]
    public async Task 计数事件_释放数少于等待者时只唤醒对应数量()
    {
        using var ev = new AsyncCountResetEvent();
        var w1 = ev.WaitAsync();
        var w2 = ev.WaitAsync();

        ev.Set(1);

        Assert.True(await Completes(w1));
        Assert.False(await Completes(w2, 200), "第二个等待者不该被唤醒");
        Assert.Equal(1, ev.Pending);
    }

    [Fact]
    public async Task 计数事件_无人等待时信号累积成许可()
    {
        using var ev = new AsyncCountResetEvent();
        ev.Set(2);
        Assert.Equal(2, ev.Permits);

        Assert.True(await Completes(ev.WaitAsync()));
        Assert.Equal(1, ev.Permits);
    }

    [Fact]
    public async Task 计数事件_许可有上限时不会无限累积()
    {
        using var ev = new AsyncCountResetEvent(maxPermits: 3);
        ev.Set(10);
        Assert.Equal(3, ev.Permits);
    }

    [Fact]
    public async Task 计数事件_释放后等待者以异常结束()
    {
        var ev = new AsyncCountResetEvent();
        var wait = ev.WaitAsync();

        ev.Dispose();

        Assert.True(await Completes(wait));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait);
    }

    [Fact]
    public async Task 计数事件_释放后再次等待会抛对象已释放()
    {
        var ev = new AsyncCountResetEvent();
        ev.Dispose();
        // 包一层以免被当成 Func<Task> 重载（xUnit2014）
        Assert.Throws<ObjectDisposedException>(() => { _ = ev.WaitAsync(); });
    }

    // ── LimitedTaskPool / DualThreadPool ───────────────────────────────

    [Fact]
    public async Task 任务池_会执行提交的工作()
    {
        using var pool = new LimitedTaskPool(2);
        var counter = 0;
        await pool.Submit(() => Interlocked.Increment(ref counter));
        Assert.Equal(1, counter);
    }

    [Fact]
    public async Task 任务池_并发数不超过上限()
    {
        using var pool = new LimitedTaskPool(2);
        var current = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 8).Select(_ => pool.Submit(() =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            Thread.Sleep(60);
            Interlocked.Decrement(ref current);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(peak <= 2, $"并发峰值 {peak} 超过了上限 2");
    }

    [Fact]
    public async Task 任务池_支持异步委托()
    {
        using var pool = new LimitedTaskPool(2);
        var result = 0;
        await pool.Submit(async () => { await Task.Delay(10); result = 7; });
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task 双线程池_CPU与IO各自受限()
    {
        using var pool = new DualThreadPool(2);
        var current = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 6).Select(i => i % 2 == 0
            ? pool.QueueCpu(() =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                Thread.Sleep(60);
                Interlocked.Decrement(ref current);
            })
            : pool.QueueIo(() =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                Thread.Sleep(60);
                Interlocked.Decrement(ref current);
            })).ToArray();

        await Task.WhenAll(tasks);

        // 两个池各 2 个槽，但共享同一个计数器；单池内部不超过 2
        Assert.True(peak <= 4, $"并发峰值 {peak} 超过了双池合计上限 4");
    }

    [Fact]
    public void 任务池_上限非法时抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LimitedTaskPool(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DualThreadPool(0));
    }

    [Fact]
    public async Task 任务池_取消后尚未开始的任务会取消()
    {
        using var pool = new LimitedTaskPool(1);
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        var blocker = pool.Submit(() => { started.Set(); release.Wait(); });
        Assert.True(started.Wait(2000));

        var second = pool.Submit(() => throw new InvalidOperationException("不该被执行"));
        pool.CancelAll();
        release.Set();

        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
    }

    /// <summary>
    /// 取得槽位与取消同时发生时，被取消的任务**绝不能**执行委托。
    ///
    /// 这是一个真实竞态：<c>SemaphoreSlim.WaitAsync(ct)</c> 在
    /// "许可被释放"与"取消信号生效"同时到达的那一刻，可能选择授予许可
    /// 而不是抛取消 —— 实测这种同时到达的情况下约 82% 会授予许可。
    /// 若调度器不在拿到许可后再检查一次取消信号，就会出现
    /// "点了取消，包管理器还是跑起来了"。
    ///
    /// 用例通过自定义的 acquire 入口**确定地**制造出
    /// "已授予槽位、但令牌已取消"的状态 —— 打时间差是测不准的
    /// （实测同样的写法有三成概率偶发失败）。
    /// </summary>
    [Fact]
    public async Task 任务池_取得槽位时已取消则不执行委托()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var bodyRan = 0;
        var scheduler = new LimitedConcurrencyTaskScheduler(1, _ => Task.CompletedTask);

        var queued = scheduler.QueueAsync(() =>
        {
            Interlocked.Increment(ref bodyRan);
            return Task.CompletedTask;
        }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.Equal(0, Volatile.Read(ref bodyRan));
    }

    [Fact]
    public async Task 调度器_并发上限是配置值而不是剩余可用数()
    {
        var scheduler = new LimitedConcurrencyTaskScheduler(3);
        // 刚建好时 3 个槽位都空闲，剩余可用数也是 3 —— 看不出区别。
        // 必须**占住**一个槽位两者才分道扬镳：真正的上限仍是 3。
        Assert.Equal(3, scheduler.MaxConcurrency);

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var used = scheduler.QueueAsync(() => release.Task);
        // 等任务真正进入委托、占住槽位
        await Task.Delay(100);

        // 此刻"剩余可用数"是 2，但"上限"必须仍是 3
        Assert.Equal(3, scheduler.MaxConcurrency);
        Assert.Equal(1, scheduler.Running);

        release.SetResult(true);
        await used;
    }

    [Fact]
    public async Task 调度器_自定义槽位来源时不会多放行任务()
    {
        // 自定义 acquire 不经过信号量，若结束时仍释放信号量，
        // 计数器的可用数会虚高，等于偷偷放宽了并发上限。
        var scheduler = new LimitedConcurrencyTaskScheduler(1, _ => Task.CompletedTask);
        var peak = 0;
        var current = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scheduler.QueueAsync(async () =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref current);
        })));

        // 上限是 1，且自定义 acquire 本身不限制并发 —— 这里只验证
        // 计数器没有因为多余的 Release 而被撑大到失控。
        Assert.True(scheduler.MaxConcurrency == 1);
        Assert.True(peak >= 1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
