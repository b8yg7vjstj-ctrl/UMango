using UMango.Core.Threading;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 并发原语测试。
///
/// 这些原语直接解决适配器并发探测的问题：
///   · AsyncDebounce：搜索框输入防抖
///   · Throttle：刷新按钮防重复
///   · LimitedConcurrency：同时起进程的数量上限
/// </summary>
public class ThreadingTests
{
    // ================= AsyncDebounce =================

    [Fact]
    public async Task 防抖只执行最后一次()
    {
        var debounce = new AsyncDebounce(delayMs: 50);
        var executed = 0;
        await debounce.InvokeAsync(() => { executed++; return Task.CompletedTask; });
        await Task.Delay(100); // 等待完成
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task 连续触发只执行最后一次()
    {
        var debounce = new AsyncDebounce(delayMs: 100);
        var lastValue = "";
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 5; i++)
        {
            var val = i.ToString();
            await debounce.InvokeAsync(() => { lastValue = val; return Task.CompletedTask; }, cts.Token);
            await Task.Delay(20); // 不到 delayMs 就触发下一次
        }

        await Task.Delay(200); // 等最后一个执行完
        Assert.Equal("4", lastValue);
    }

    [Fact]
    public async Task 取消后不执行()
    {
        var debounce = new AsyncDebounce(delayMs: 200);
        var executed = false;

        var task = debounce.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            executed = true;
        });

        debounce.Cancel();
        await Task.Delay(300);
        Assert.False(executed);
    }

    // ================= Throttle =================

    [Fact]
    public async Task 节流允许第一次立即执行()
    {
        var throttle = new Throttle(minDelayMs: 100);
        var executed = false;
        var result = await throttle.InvokeAsync(() => { executed = true; return Task.CompletedTask; });
        Assert.True(result);
        Assert.True(executed);
    }

    [Fact]
    public async Task 连续触发第二次被跳过()
    {
        var throttle = new Throttle(minDelayMs: 200);
        var count = 0;

        await throttle.InvokeAsync(() => { count++; return Task.CompletedTask; });
        var second = await throttle.InvokeAsync(() => { count++; return Task.CompletedTask; });

        Assert.False(second); // 被节流
        Assert.Equal(1, count); // 只执行了一次
    }

    // ================= LimitedConcurrency =================

    [Fact]
    public async Task 并发上限生效()
    {
        var scheduler = new LimitedConcurrencyTaskScheduler(maxConcurrency: 2);
        var running = 0;
        var maxConcurrent = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            scheduler.QueueAsync(async () =>
            {
                var current = Interlocked.Increment(ref running);
                lock (_gate) { maxConcurrent = Math.Max(maxConcurrent, current); }
                await Task.Delay(50);
                Interlocked.Decrement(ref running);
            }));

        await Task.WhenAll(tasks);

        Assert.True(maxConcurrent <= 2, $"最大并发 {maxConcurrent} 超过了上限 2");
    }

    private static readonly object _gate = new();
}
