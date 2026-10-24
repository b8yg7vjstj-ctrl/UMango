using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Lifecycle;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 生命周期编排的行为。
///
/// 这些用例不涉及任何真实系统资源，因此可以在 macOS 上完整运行。
/// </summary>
public class LifecycleManagerTests
{
    /// <summary>记录启动停止顺序的服务桩。</summary>
    private sealed class Probe : LifecycleServiceBase
    {
        private readonly List<string> _log;

        public Probe(string id, List<string> log, bool supportAsync = true)
        {
            Identifier = id;
            _log = log;
            SupportAsync = supportAsync;
        }

        public override string Identifier { get; }
        public override string Name => Identifier;
        public override bool SupportAsync { get; }
        public bool ThrowOnStart { get; set; }
        public bool ThrowOnStop { get; set; }

        public override void Start()
        {
            if (ThrowOnStart) throw new InvalidOperationException("启动失败");
            _log.Add("start:" + Identifier);
        }

        public override void Stop()
        {
            if (ThrowOnStop) throw new InvalidOperationException("停止失败");
            _log.Add("stop:" + Identifier);
        }
    }

    [Fact]
    public async Task 没注册任何服务时启动阶段不会出错()
    {
        var manager = new LifecycleManager();
        await manager.StartStageAsync(LifecycleState.Loading);
        Assert.Equal(0, manager.StartedCount);
    }

    [Fact]
    public async Task 只启动指定阶段的服务()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("a", log), new ServiceRegistration(LifecycleState.Loading));
        manager.Register(new Probe("b", log), new ServiceRegistration(LifecycleState.Running));

        await manager.StartStageAsync(LifecycleState.Loading);

        Assert.Equal(["start:a"], log);
        Assert.Equal(1, manager.StartedCount);
    }

    [Fact]
    public async Task 同阶段按优先级降序启动()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("low", log), new ServiceRegistration(LifecycleState.Loading, 1));
        manager.Register(new Probe("high", log), new ServiceRegistration(LifecycleState.Loading, 10));
        manager.Register(new Probe("mid", log), new ServiceRegistration(LifecycleState.Loading, 5));

        await manager.StartStageAsync(LifecycleState.Loading);

        Assert.Equal(["start:high", "start:mid", "start:low"], log);
    }

    [Fact]
    public async Task 同步服务先于异步服务启动()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        // 同步服务给更高优先级，但异步服务仍应先被启动（同步优先于异步）。
        manager.Register(new Probe("async", log, supportAsync: true), new ServiceRegistration(LifecycleState.Loading, 100));
        manager.Register(new Probe("sync", log, supportAsync: false), new ServiceRegistration(LifecycleState.Loading, 1));

        await manager.StartStageAsync(LifecycleState.Loading);

        Assert.Equal(["start:sync", "start:async"], log);
    }

    [Fact]
    public async Task 停止时按启动的逆序()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("a", log), new ServiceRegistration(LifecycleState.Loading, 1));
        manager.Register(new Probe("b", log), new ServiceRegistration(LifecycleState.Loading, 10));

        await manager.StartStageAsync(LifecycleState.Loading);
        await manager.StopAllAsync();

        Assert.Equal(["start:b", "start:a", "stop:a", "stop:b"], log);
    }

    [Fact]
    public async Task 停止时跳过没启动过的服务()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("a", log), new ServiceRegistration(LifecycleState.Loading));

        await manager.StopAllAsync();

        Assert.Empty(log);
        Assert.Equal(0, manager.StartedCount);
    }

    [Fact]
    public async Task 某个服务停止失败不影响其它服务()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("a", log), new ServiceRegistration(LifecycleState.Loading, 10));
        manager.Register(new Probe("b", log) { ThrowOnStop = true }, new ServiceRegistration(LifecycleState.Loading, 5));

        await manager.StartStageAsync(LifecycleState.Loading);
        await manager.StopAllAsync();

        Assert.Contains("stop:a", log);
    }

    [Fact]
    public void 标识重复时拒绝注册()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("dup", log), new ServiceRegistration(LifecycleState.Loading));
        Assert.Throws<InvalidOperationException>(
            () => manager.Register(new Probe("dup", log), new ServiceRegistration(LifecycleState.Running)));
    }

    [Fact]
    public void 可以取消注册()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        var probe = new Probe("a", log);
        manager.Register(probe, new ServiceRegistration(LifecycleState.Loading));
        Assert.True(manager.Unregister(probe));
        Assert.False(manager.Unregister(probe));
        Assert.Empty(manager.RegisteredServices);
    }

    [Fact]
    public async Task 可以按类上的特性注册()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new AttributedService(log));
        await manager.StartStageAsync(LifecycleState.Loaded);
        Assert.Equal(["start:attributed"], log);
    }

    [Fact]
    public async Task 没有特性的服务默认手动运行()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("plain", log), new ServiceRegistration(LifecycleState.Manual));
        await manager.StartStageAsync(LifecycleState.Loading);
        Assert.Empty(log);
    }

    [Fact]
    public void 上下文可以请求退出且只算一次()
    {
        var context = new LifecycleContext();
        var count = 0;
        context.ExitRequestedChanged += () => count++;

        Assert.False(context.ExitRequested);
        context.RequestExit();
        context.RequestExit();

        Assert.True(context.ExitRequested);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task 取消令牌能中断启动()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("a", log), new ServiceRegistration(LifecycleState.Loading));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartStageAsync(LifecycleState.Loading, cts.Token));
        Assert.Empty(log);
    }

    [Fact]
    public void 注册空服务会被拒绝()
    {
        var manager = new LifecycleManager();
        Assert.Throws<ArgumentNullException>(() => manager.Register(null!, new ServiceRegistration(LifecycleState.Loading)));
    }

    [Fact]
    public async Task 启动失败会向上抛()
    {
        var log = new List<string>();
        var manager = new LifecycleManager();
        manager.Register(new Probe("bad", log) { ThrowOnStart = true }, new ServiceRegistration(LifecycleState.Loading));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartStageAsync(LifecycleState.Loading));
    }

    /// <summary>带特性标记的服务。</summary>
    [LifecycleService(LifecycleState.Loaded, Priority = 3)]
    private sealed class AttributedService : LifecycleServiceBase
    {
        private readonly List<string> _log;

        public AttributedService(List<string> log) => _log = log;

        public override string Identifier => "attributed";
        public override string Name => "带特性的服务";
        public override bool SupportAsync => false;

        public override void Start() => _log.Add("start:attributed");
    }
}
