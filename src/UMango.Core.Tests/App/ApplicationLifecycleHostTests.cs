// SPDX-License-Identifier: Apache-2.0
//
// 应用生命周期宿主（ApplicationLifecycleHost）的单元测试。
//
// 设计说明取自 PCL-CE 的 PCL.Core/App/Essentials/ApplicationService.cs
// 与 MainWindowService.cs（Apache-2.0）。

using UMango.Core.App.Essentials;
using Xunit;

namespace UMango.Core.Tests.App;

[Collection("静态状态")]
public class ApplicationLifecycleHostTests
{
    [Fact]
    public void 启动会创建应用与主窗口()
    {
        var host = new ApplicationLifecycleHost
        {
            ApplicationFactory = () => new object(),
            MainWindowFactory = () => new object(),
        };
        host.Start();
        Assert.NotNull(host.Application);
        Assert.NotNull(host.MainWindow);
        Assert.Equal(MainWindowState.Created, host.WindowState);
    }

    [Fact]
    public void 未登记工厂时启动不抛异常()
    {
        var host = new ApplicationLifecycleHost();
        host.Start();
        Assert.Null(host.Application);
        Assert.Null(host.MainWindow);
        Assert.Equal(MainWindowState.None, host.WindowState);
        Assert.True(host.IsStarted);
    }

    [Fact]
    public void 服务按优先级升序启动()
    {
        var order = new List<string>();
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("late", () => order.Add("late"), priority: 10));
        host.Register(new LifecycleServiceRegistration("early", () => order.Add("early"), priority: -10));
        host.Start();
        Assert.Equal(new[] { "early", "late" }, order);
    }

    [Fact]
    public void 同优先级按标识排序()
    {
        var order = new List<string>();
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("b", () => order.Add("b")));
        host.Register(new LifecycleServiceRegistration("a", () => order.Add("a")));
        host.Start();
        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void 重复登记同一标识被拒绝()
    {
        var host = new ApplicationLifecycleHost();
        Assert.True(host.Register(new LifecycleServiceRegistration("x", () => { })));
        Assert.False(host.Register(new LifecycleServiceRegistration("x", () => { })));
        Assert.Equal(new[] { "x" }, host.RegisteredServiceIds);
    }

    [Fact]
    public void 单个服务抛异常不影响其它服务()
    {
        var started = new List<string>();
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("boom", () => throw new InvalidOperationException("x"), priority: 0));
        host.Register(new LifecycleServiceRegistration("ok", () => started.Add("ok"), priority: 1));
        host.Start();
        Assert.Equal(new[] { "ok" }, started);
    }

    [Fact]
    public void 停止按启动的逆序执行()
    {
        var order = new List<string>();
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("a", () => { }, () => order.Add("a"), priority: 0));
        host.Register(new LifecycleServiceRegistration("b", () => { }, () => order.Add("b"), priority: 1));
        host.Start();
        host.Stop();
        Assert.Equal(new[] { "b", "a" }, order);
    }

    [Fact]
    public void 没有停止动作的服务被跳过()
    {
        var stopped = 0;
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("a", () => { }));
        host.Start();
        host.Stop();
        Assert.Equal(0, stopped);
    }

    [Fact]
    public void 单个停止动作抛异常不影响其它停止动作()
    {
        var order = new List<string>();
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("a", () => { }, () => order.Add("a"), priority: 0));
        host.Register(new LifecycleServiceRegistration("b", () => { }, () => throw new InvalidOperationException("x"), priority: 1));
        host.Start();
        host.Stop();
        Assert.Equal(new[] { "a" }, order);
    }

    [Fact]
    public void 重复启动只执行一次()
    {
        var count = 0;
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("a", () => count++));
        host.Start();
        host.Start();
        Assert.Equal(1, count);
    }

    [Fact]
    public void 重复停止只执行一次()
    {
        var count = 0;
        var host = new ApplicationLifecycleHost();
        host.Register(new LifecycleServiceRegistration("a", () => { }, () => count++));
        host.Start();
        host.Stop();
        host.Stop();
        Assert.Equal(1, count);
    }

    [Fact]
    public void 窗口Loaded后状态变为就绪()
    {
        var host = new ApplicationLifecycleHost { MainWindowFactory = () => new object() };
        host.Start();
        Assert.Equal(MainWindowState.Created, host.WindowState);
        host.NotifyMainWindowLoaded();
        Assert.Equal(MainWindowState.Ready, host.WindowState);
    }

    [Fact]
    public void 没有窗口时通知Loaded不改变状态()
    {
        var host = new ApplicationLifecycleHost();
        host.Start();
        host.NotifyMainWindowLoaded();
        Assert.Equal(MainWindowState.None, host.WindowState);
    }

    [Fact]
    public void 作用域可登记且不重复()
    {
        var host = new ApplicationLifecycleHost();
        Assert.True(host.RegisterScope(new LifecycleScope("application", "应用程序")));
        Assert.False(host.RegisterScope(new LifecycleScope("application", "应用程序")));
        Assert.Single(host.Scopes);
        Assert.Equal("application", host.Scopes[0].Id);
    }

    [Fact]
    public void 服务已启动标记在启动后为真()
    {
        var registration = new LifecycleServiceRegistration("a", () => { });
        var host = new ApplicationLifecycleHost();
        host.Register(registration);
        Assert.False(registration.Started);
        host.Start();
        Assert.True(registration.Started);
    }
}
