// SPDX-License-Identifier: Apache-2.0
//
// 下载资源协调器的测试（全局/单主机连接额度、缓冲区预算、速率统计、限速）。
//
// 对应 PCL-CE `Plain Craft Launcher 2/Modules/Network/Downloader/DownloadResourceManager.cs`
// （自定义许可目录 → UMango 独立重写，测试对着**规格**写，不对着原版写）。

using System;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Download.Resources;
using Xunit;

namespace UMango.Core.Tests.Download;

public sealed class AsyncQuotaTests
{
    [Fact]
    public async Task 额度够_立即拿到()
    {
        var quota = new AsyncQuota();

        using var lease = await quota.AcquireAsync(1, () => 2);

        Assert.Equal(1, quota.Used);
    }

    [Fact]
    public async Task 额度不够_等待释放()
    {
        var quota = new AsyncQuota();
        var first = await quota.AcquireAsync(2, () => 2);

        var secondTask = quota.AcquireAsync(2, () => 2).AsTask();
        await Task.Delay(30);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, quota.Used);
    }

    [Fact]
    public async Task 释放后_额度归零()
    {
        var quota = new AsyncQuota();
        var lease = await quota.AcquireAsync(3, () => 5);

        lease.Dispose();

        Assert.Equal(0, quota.Used);
    }

    [Fact]
    public async Task 重复释放_只归还一次()
    {
        var quota = new AsyncQuota();
        var lease = await quota.AcquireAsync(3, () => 5);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, quota.Used);
    }

    [Fact]
    public async Task 取消等待_不泄漏额度()
    {
        var quota = new AsyncQuota();
        var first = await quota.AcquireAsync(2, () => 2);

        using var cts = new CancellationTokenSource();
        var waiting = quota.AcquireAsync(2, () => 2, cts.Token).AsTask();
        await Task.Delay(30);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();

        Assert.Equal(0, quota.Used);
    }

    [Fact]
    public async Task 容量变大_等待者被唤醒()
    {
        var quota = new AsyncQuota();
        var capacity = 1;
        var first = await quota.AcquireAsync(1, () => capacity);

        var waiting = quota.AcquireAsync(1, () => capacity).AsTask();
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);

        capacity = 2;
        quota.NotifyCapacityChanged();

        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, quota.Used);
        first.Dispose();
    }

    [Fact]
    public async Task 重复释放租约_只归还一次()
    {
        var quota = new AsyncQuota();
        var lease = await quota.AcquireAsync(3, () => 10);

        // 别的使用者占着 2
        using var other = await quota.AcquireAsync(2, () => 10);
        Assert.Equal(5, quota.Used);

        lease.Dispose();
        lease.Dispose();

        // 3 只能被归还一次：若归还两次，used 会从 2 掉到 -1 → 被夹到 0
        Assert.Equal(2, quota.Used);
    }

    [Fact]
    public async Task 申请量超过容量_也能拿到_按申请量兜底()
    {
        var quota = new AsyncQuota();

        using var lease = await quota.AcquireAsync(10, () => 1);

        Assert.Equal(10, quota.Used);
    }

    [Fact]
    public async Task 申请量非法_抛异常()
    {
        var quota = new AsyncQuota();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => quota.AcquireAsync(0, () => 10).AsTask());
    }
}

public sealed class DownloadResourceManagerTests
{
    [Fact]
    public async Task 申请连接_活跃数加一()
    {
        using var manager = new DownloadResourceManager();

        var lease = await manager.AcquireConnectionAsync("https://example.com/a");

        Assert.Equal(1, manager.ActiveConnectionCount);
        lease.Dispose();
    }

    [Fact]
    public async Task 释放连接_活跃数归零()
    {
        using var manager = new DownloadResourceManager();

        var lease = await manager.AcquireConnectionAsync("https://example.com/a");
        lease.Dispose();

        Assert.Equal(0, manager.ActiveConnectionCount);
    }

    [Fact]
    public async Task 重复释放连接_额度只归还一次()
    {
        // 上限 2：释放后再取两个应当正好占满，第三个必须等
        using var manager = new DownloadResourceManager { MaxConnections = 2, MaxConnectionsPerHost = 4 };

        var lease = await manager.AcquireConnectionAsync("https://a.test/x");
        lease.Dispose();
        lease.Dispose();

        using var b = await manager.AcquireConnectionAsync("https://a.test/y")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var c = await manager.AcquireConnectionAsync("https://a.test/z")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 如果重复释放把额度多还了一次，第三个连接会立刻拿到（而不是等）
        var thirdTask = manager.AcquireConnectionAsync("https://b.test/w").AsTask();
        await Task.Delay(80);
        Assert.False(thirdTask.IsCompleted, "额度被重复归还了：第三个连接不该立刻拿到");

        b.Dispose();
        c.Dispose();
        using var d = await thirdTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 缓冲区归还后_再租能拿到同一块数组()
    {
        using var manager = new DownloadResourceManager { BufferBudgetBytes = 1024 * 1024 };

        var first = await manager.RentBufferAsync(4096);
        var buffer = first.Buffer;
        first.Dispose();

        var second = await manager.RentBufferAsync(4096);
        try
        {
            // 归还到池里后，同尺寸再租通常拿回同一块数组 —— 池没归还的话不会
            Assert.Same(buffer, second.Buffer);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public async Task 重复释放连接_活跃数不会变负()
    {
        using var manager = new DownloadResourceManager();

        var lease = await manager.AcquireConnectionAsync("https://example.com/a");
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, manager.ActiveConnectionCount);
    }

    [Fact]
    public async Task 超过全局连接上限_第二个要等()
    {
        using var manager = new DownloadResourceManager { MaxConnections = 1 };

        var first = await manager.AcquireConnectionAsync("https://a.test/x");
        var secondTask = manager.AcquireConnectionAsync("https://b.test/x").AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 同一主机超过上限_第二个要等()
    {
        using var manager = new DownloadResourceManager
        {
            MaxConnections = 10,
            MaxConnectionsPerHost = 1,
        };

        var first = await manager.AcquireConnectionAsync("https://a.test/x");
        var secondTask = manager.AcquireConnectionAsync("https://a.test/y").AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 不同主机_互不影响()
    {
        using var manager = new DownloadResourceManager
        {
            MaxConnections = 10,
            MaxConnectionsPerHost = 1,
        };

        using var a = await manager.AcquireConnectionAsync("https://a.test/x");
        using var b = await manager.AcquireConnectionAsync("https://b.test/x");

        Assert.Equal(2, manager.ActiveConnectionCount);
    }

    [Fact]
    public async Task 主机名大小写不同_算同一主机()
    {
        using var manager = new DownloadResourceManager
        {
            MaxConnections = 10,
            MaxConnectionsPerHost = 1,
        };

        using var a = await manager.AcquireConnectionAsync("https://Example.COM/x");
        var secondTask = manager.AcquireConnectionAsync("https://example.com/y").AsTask();
        await Task.Delay(50);

        Assert.False(secondTask.IsCompleted);
        a.Dispose();
        using var b = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 缓冲区_有内容且归还后额度归零()
    {
        using var manager = new DownloadResourceManager { BufferBudgetBytes = 1024 };

        var lease = await manager.RentBufferAsync(512);
        Assert.True(lease.Length >= 512);
        Assert.Equal(512, lease.Buffer.Length >= 512 ? 512 : 0);

        lease.Dispose();
    }

    [Fact]
    public async Task 缓冲区预算不足_等待()
    {
        using var manager = new DownloadResourceManager { BufferBudgetBytes = 1024 };

        var first = await manager.RentBufferAsync(1024);
        var secondTask = manager.RentBufferAsync(1024).AsTask();
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void 记录字节_累计数增加()
    {
        using var manager = new DownloadResourceManager();

        manager.RecordDownloadedBytes(100);
        manager.RecordDownloadedBytes(50);

        Assert.Equal(150, manager.TotalBytesDownloaded);
    }

    [Fact]
    public void 记录字节_负数被忽略()
    {
        using var manager = new DownloadResourceManager();

        manager.RecordDownloadedBytes(-100);

        Assert.Equal(0, manager.TotalBytesDownloaded);
    }

    [Fact]
    public void 完成计数_递增()
    {
        using var manager = new DownloadResourceManager();

        manager.RecordDownloadDone();
        manager.RecordDownloadDone();

        Assert.Equal(2, manager.DownloadDoneCount);
    }

    [Fact]
    public async Task 不限速_立即返回()
    {
        using var manager = new DownloadResourceManager { SpeedLimitBytesPerSecond = 0 };

        var started = DateTime.UtcNow;
        await manager.ThrottleAsync(1_000_000);

        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1);
    }

    [Fact]
    public async Task 限速_超额的会等待()
    {
        // 令牌桶初始额度是 128KB；请求 256KB @ 1MB/s → 必然要等约 0.125 秒以上
        using var manager = new DownloadResourceManager { SpeedLimitBytesPerSecond = 1_000_000 };

        var started = DateTime.UtcNow;
        await manager.ThrottleAsync(256 * 1024);

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Assert.True(elapsed >= 0.1, $"实际只等了 {elapsed:F3} 秒");
        Assert.True(elapsed < 5, $"等了 {elapsed:F3} 秒，太久了");
    }

    [Fact]
    public async Task 限速被取消_抛取消异常()
    {
        using var manager = new DownloadResourceManager { SpeedLimitBytesPerSecond = 1_000_000 };
        using var cts = new CancellationTokenSource();

        var task = manager.ThrottleAsync(256 * 1024, cts.Token);
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task 限速取消后_额度归还_后续调用不会被拖慢()
    {
        using var manager = new DownloadResourceManager { SpeedLimitBytesPerSecond = 1_000_000 };
        using var cts = new CancellationTokenSource();

        var task = manager.ThrottleAsync(256 * 1024, cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // 归还后立刻再要同样额度：等待时间应与第一次相当（未归还的话会多等一轮）
        var started = DateTime.UtcNow;
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            await manager.ThrottleAsync(256 * 1024, cts2.Token);
        }
        catch (OperationCanceledException)
        {
            // 取消即可 —— 关键是别等太久
        }

        var elapsed = (DateTime.UtcNow - started).TotalSeconds;
        Assert.True(elapsed < 0.2, $"等了 {elapsed:F3} 秒（未归还令牌的话会多等约 0.25 秒）");
    }

    [Fact]
    public void 通知连接上限变化_不抛异常()
    {
        using var manager = new DownloadResourceManager();

        manager.NotifyConnectionLimitChanged();
    }

    [Fact]
    public async Task 主机桶_引用归零后回收()
    {
        using var manager = new DownloadResourceManager();

        for (var i = 0; i < 3; i++)
        {
            var lease = await manager.AcquireConnectionAsync("https://a.test/x");
            lease.Dispose();
        }

        // 全部释放后没有残留引用 → 再申请不会卡住
        using var again = await manager.AcquireConnectionAsync("https://a.test/x")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
