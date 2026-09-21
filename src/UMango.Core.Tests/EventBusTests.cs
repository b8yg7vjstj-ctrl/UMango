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
}
