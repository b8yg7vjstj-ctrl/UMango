using System;
using System.IO;
using System.Threading.Tasks;
using UMango.Core.App.SingleInstance;
using Xunit;

namespace UMango.Core.Tests.App;

/// <summary>
/// 单实例保护的测试。用临时目录当锁文件路径，不碰真实用户目录。
/// </summary>
public class SingleInstanceGuardTests
{
    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), "umango-si-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 第一个实例能获得锁()
    {
        var lockFile = NewTempPath();
        await using var guard = new SingleInstanceGuard(lockFile, "pipe-" + Guid.NewGuid().ToString("N"), _ => Task.CompletedTask);
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public async Task 第二个实例拿不到锁()
    {
        var lockFile = NewTempPath();
        await using var first = new SingleInstanceGuard(lockFile, "pipe-first", _ => Task.CompletedTask);
        Assert.True(first.TryAcquire());

        await using var second = new SingleInstanceGuard(lockFile, "pipe-second", _ => Task.CompletedTask);
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public async Task 释放后可以再次获得锁()
    {
        var lockFile = NewTempPath();
        await using (var guard = new SingleInstanceGuard(lockFile, "pipe-a", _ => Task.CompletedTask))
        {
            Assert.True(guard.TryAcquire());
        }

        // 上面已 Dispose（删锁文件）→ 现在应能重新获得
        await using var again = new SingleInstanceGuard(lockFile, "pipe-b", _ => Task.CompletedTask);
        Assert.True(again.TryAcquire());
    }

    [Fact]
    public async Task 通知消息能送达首个实例()
    {
        var lockFile = NewTempPath();
        var pipeName = "pipe-msg-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var first = new SingleInstanceGuard(lockFile, pipeName, msg =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        });
        Assert.True(first.TryAcquire());

        var ok = await SingleInstanceGuard.TryNotifyExistingAsync(lockFile, "activate");
        Assert.True(ok);

        var finished = await Task.WhenAny(received.Task, Task.Delay(3000));
        var msg = finished == received.Task ? await received.Task : "<超时>";
        Assert.Equal("activate", msg);
    }

    [Fact]
    public async Task 无实例时通知返回false而不是抛异常()
    {
        var lockFile = NewTempPath();
        var ok = await SingleInstanceGuard.TryNotifyExistingAsync(lockFile, "activate");
        Assert.False(ok);
    }

    [Fact]
    public async Task 锁文件不存在时通知也安全()
    {
        var lockFile = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz", "instance.lock");
        // 不应抛异常，且必须同步返回 false（不挂住）
        var result = await SingleInstanceGuard.TryNotifyExistingAsync(lockFile, "activate");
        Assert.False(result);
    }

    [Fact]
    public async Task 释放后锁文件被删除()
    {
        var lockFile = NewTempPath();
        await using (var guard = new SingleInstanceGuard(lockFile, "pipe-del", _ => Task.CompletedTask))
        {
            Assert.True(guard.TryAcquire());
            Assert.True(File.Exists(lockFile));
        }

        Assert.False(File.Exists(lockFile), "Dispose 后必须删掉锁文件，否则残留的过期管道名会误导第二个实例");
    }

    [Fact]
    public async Task 残留锁文件不会永久挡住新实例()
    {
        var lockFile = NewTempPath();
        // 模拟"首个实例被强杀"：锁文件还在（指向一个已不存在的管道），
        // 但没有任何进程持有互斥、也没有人在听那个管道。
        // 注意：这里**不能**再调 TryAcquire()，否则它会用真实管道名覆盖锁文件，
        // 通知就会连到活管道上 —— 那测的就不是"强杀残留"了。
        File.WriteAllText(lockFile, "pipe-of-dead-instance");

        // 新实例应能拿到互斥（残留的是文件，不是互斥）
        await using var guard = new SingleInstanceGuard(lockFile, "pipe-new", _ => Task.CompletedTask);
        Assert.True(guard.TryAcquire(), "残留锁文件不应挡住新实例");

        // 覆盖锁文件为死管道名后再通知：必须失败而不是挂住
        File.WriteAllText(lockFile, "pipe-of-dead-instance");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var notified = await SingleInstanceGuard.TryNotifyExistingAsync(lockFile, "activate");
        sw.Stop();
        Assert.False(notified, "连不上的管道应返回 false，让调用方自行退出");
        // 上限给 10 秒：语义是「不能挂死」，不是精确计时。
        Assert.True(sw.ElapsedMilliseconds < 10000, $"不应长时间挂住（实际 {sw.ElapsedMilliseconds}ms）");
    }

    [Fact]
    public async Task 管道名写进锁文件可被第二个实例读回()
    {
        var lockFile = NewTempPath();
        var pipeName = "pipe-readback";
        await using var guard = new SingleInstanceGuard(lockFile, pipeName, _ => Task.CompletedTask);
        Assert.True(guard.TryAcquire());

        var content = File.ReadAllText(lockFile).Trim();
        Assert.Equal(pipeName, content);
    }
}
