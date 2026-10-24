using UMango.Core.Events;
using Xunit;

namespace UMango.Core.Tests;

public class EventBusTests
{
    private sealed record TestEvent(string Data);
    private sealed record OtherEvent(int Value);

    [Fact]
    public void 订阅后发布能收到()
    {
        var bus = new EventBus();
        var received = "";
        bus.Subscribe<TestEvent>(e => received = e.Data);
        bus.Publish(new TestEvent("hello"));
        Assert.Equal("hello", received);
    }

    [Fact]
    public void 不同事件类型互不干扰()
    {
        var bus = new EventBus();
        var testReceived = false;
        var otherReceived = false;
        bus.Subscribe<TestEvent>(_ => testReceived = true);
        bus.Subscribe<OtherEvent>(_ => otherReceived = true);
        bus.Publish(new TestEvent("x"));
        Assert.True(testReceived);
        Assert.False(otherReceived);
    }

    [Fact]
    public void 取消订阅后不再收到()
    {
        var bus = new EventBus();
        var count = 0;
        using var sub = bus.Subscribe<TestEvent>(_ => count++);
        bus.Publish(new TestEvent("a"));
        sub.Dispose();
        bus.Publish(new TestEvent("b"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void 订阅者异常不阻塞其它订阅者()
    {
        var bus = new EventBus();
        var ok = false;
        bus.Subscribe<TestEvent>(_ => throw new Exception("boom"));
        bus.Subscribe<TestEvent>(_ => ok = true);
        bus.Publish(new TestEvent("x"));
        Assert.True(ok);
    }

    #region 通道

    [Fact]
    public async Task 通道订阅能收到发布()
    {
        var bus = new EventBus();
        var received = "";
        bus.Subscribe<TestEvent>("ch", e => { received = e.Data; return Task.CompletedTask; });
        await bus.PublishAsync("ch", new TestEvent("hi"));
        Assert.Equal("hi", received);
    }

    [Fact]
    public async Task 不同通道互不干扰()
    {
        var bus = new EventBus();
        var a = 0;
        var b = 0;
        bus.Subscribe<TestEvent>("a", _ => { a++; return Task.CompletedTask; });
        bus.Subscribe<TestEvent>("b", _ => { b++; return Task.CompletedTask; });
        await bus.PublishAsync("a", new TestEvent("x"));
        Assert.Equal(1, a);
        Assert.Equal(0, b);
    }

    [Fact]
    public async Task 通道取消订阅后不再收到()
    {
        var bus = new EventBus();
        var count = 0;
        var sub = bus.Subscribe<TestEvent>("ch", _ => { count++; return Task.CompletedTask; });
        await bus.PublishAsync("ch", new TestEvent("a"));
        sub.Dispose();
        await bus.PublishAsync("ch", new TestEvent("b"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task 向不存在的通道发布会抛异常()
    {
        var bus = new EventBus();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.PublishAsync("不存在", new TestEvent("x")));
    }

    [Fact]
    public void 订阅不存在的通道会自动创建()
    {
        var bus = new EventBus();
        bus.Subscribe<TestEvent>("auto", _ => Task.CompletedTask);
        Assert.Contains("auto", bus.Channels);
    }

    [Fact]
    public void 显式创建通道是幂等的()
    {
        var bus = new EventBus();
        Assert.True(bus.AddChannel("ch"));
        Assert.False(bus.AddChannel("ch"));
    }

    [Fact]
    public void 移除通道会带走它的订阅()
    {
        var bus = new EventBus();
        var count = 0;
        bus.Subscribe<TestEvent>("ch", _ => { count++; return Task.CompletedTask; });
        Assert.True(bus.RemoveChannel("ch"));
        Assert.False(bus.RemoveChannel("ch"));
        Assert.DoesNotContain("ch", bus.Channels);
    }

    [Fact]
    public async Task 订阅基类能收到派生事件()
    {
        var bus = new EventBus();
        object? received = null;
        bus.Subscribe<object>("ch", e => { received = e; return Task.CompletedTask; });
        await bus.PublishAsync("ch", new TestEvent("derived"));
        Assert.IsType<TestEvent>(received);
    }

    [Fact]
    public async Task 通道处理器异常不影响其它处理器()
    {
        var bus = new EventBus();
        var ok = false;
        bus.Subscribe<TestEvent>("ch", _ => throw new InvalidOperationException("boom"));
        bus.Subscribe<TestEvent>("ch", _ => { ok = true; return Task.CompletedTask; });
        await bus.PublishAsync("ch", new TestEvent("x"));
        Assert.True(ok);
    }

    [Fact]
    public async Task 持有者处理器能收到事件()
    {
        var bus = new EventBus();
        var handler = new CountingHandler();
        bus.Subscribe<TestEvent>("ch", handler);
        await bus.PublishAsync("ch", new TestEvent("x"));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public void 通道名为空时抛异常()
    {
        var bus = new EventBus();
        Assert.Throws<ArgumentException>(() => bus.Subscribe<TestEvent>("", _ => Task.CompletedTask));
    }

    [Fact]
    public void 通道处理器为null时抛异常()
    {
        var bus = new EventBus();
        Assert.Throws<ArgumentNullException>(() => bus.Subscribe<TestEvent>("ch", (Func<TestEvent, Task>)null!));
    }

    [Fact]
    public async Task 持有者被回收后不再收到事件()
    {
        var bus = new EventBus();

        // 持有者在另一个方法里创建：Debug 构建下局部变量会一直存活到方法结束，
        // 若与 GC.Collect() 同处一个方法，持有者永远不会被回收，测不出弱引用语义。
        var weak = SubscribeInSeparateScope(bus);
        await bus.PublishAsync("ch", new TestEvent("x"));

        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive, "事件总线不应通过弱订阅阻止持有者被回收");

        // 持有者已消失 → 不应抛异常，也不应误投递给别人
        await bus.PublishAsync("ch", new TestEvent("y"));
    }

    [Fact]
    public async Task 持有者被回收后订阅条目会被清理掉()
    {
        var bus = new EventBus();
        var weak = SubscribeInSeparateScope(bus);
        Assert.Equal(1, bus.SubscriberCount("ch"));

        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive, "事件总线不应通过弱订阅阻止持有者被回收");

        // 发布时顺手清掉已回收持有者的订阅条目，避免频道里堆积垃圾。
        await bus.PublishAsync("ch", new TestEvent("y"));
        Assert.Equal(0, bus.SubscriberCount("ch"));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SubscribeInSeparateScope(EventBus bus)
    {
        var handler = new CountingHandler();
        bus.Subscribe<TestEvent>("ch", handler);
        return new WeakReference(handler);
    }

    private sealed class CountingHandler : IEventHandler<TestEvent>
    {
        public int Count { get; private set; }

        public Task HandleEventAsync(TestEvent eventData)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    #endregion
}
