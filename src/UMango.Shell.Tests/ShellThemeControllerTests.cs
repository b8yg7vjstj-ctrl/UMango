// SPDX-License-Identifier: Apache-2.0
using UMango.Core.UI.Theme;
using UMango.Shell.Theming;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// Shell 层主题编排（<see cref="ShellThemeController"/>）的用例。
///
/// <para>
/// 这些用例之所以能跑在 macOS 上：编排层**不碰任何 UI 框架类型** ——
/// 「换资源字典」是各壳挂进来的回调，「探测系统主题」也是各壳挂进来的探测器。
/// </para>
/// </summary>
[Collection("静态状态")]
public class ShellThemeControllerTests
{
    /// <summary>记录一次宿主换字典的调用。</summary>
    private sealed class HostRecorder
    {
        public List<(ThemeKind Kind, bool Dark)> Calls { get; } = [];

        public void Apply(ThemeKind kind, bool dark) => Calls.Add((kind, dark));
    }

    /// <summary>可编程的系统主题探测器。</summary>
    private sealed class FakeProbe : ISystemThemeProbe
    {
        public SystemThemeState State { get; set; } = SystemThemeState.Unknown;

        public int Calls { get; private set; }

        public SystemThemeState Detect()
        {
            Calls++;
            return State;
        }
    }

    private static (HostRecorder Host, FakeProbe Probe) Setup()
    {
        // ShellThemeController 是静态的，测试之间要复位 —— 否则上一个用例留下的
        // 回调会串到下一个用例里。
        var host = new HostRecorder();
        var probe = new FakeProbe();
        ShellThemeController.HostApply = host.Apply;
        ShellThemeController.SystemThemeProbe = probe;
        // 复位到「PCL 经典 + 浅色」。走 Apply 是因为状态是只读的真相源，
        // 没有任何合法的旁路能直接写它（这正是「唯一真相源」的意义）。
        ShellThemeController.Apply(ThemeKind.PclClassic, dark: false);
        host.Calls.Clear();
        return (host, probe);
    }

    [Fact]
    public void 切换主题会写状态并通知宿主换字典()
    {
        var (host, _) = Setup();

        ShellThemeController.Apply(ThemeKind.Fluent10, dark: true);

        Assert.Equal(ThemeKind.Fluent10, ShellThemeController.CurrentKind);
        Assert.True(ShellThemeController.IsDark);
        Assert.Single(host.Calls);
        Assert.Equal((ThemeKind.Fluent10, true), host.Calls[0]);
    }

    [Fact]
    public void 切换深浅色只改深浅那一维()
    {
        var (host, _) = Setup();
        ShellThemeController.Apply(ThemeKind.Fluent10, dark: false);

        ShellThemeController.ToggleDark();

        Assert.Equal(ThemeKind.Fluent10, ShellThemeController.CurrentKind);
        Assert.True(ShellThemeController.IsDark);
        Assert.Equal(2, host.Calls.Count);
        Assert.Equal((ThemeKind.Fluent10, true), host.Calls[1]);
    }

    [Fact]
    public void 跟随系统时用探测结果决定深浅()
    {
        var (host, probe) = Setup();
        probe.State = SystemThemeState.Known(true);

        ShellThemeController.ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode.System);

        Assert.True(ShellThemeController.IsDark);
        Assert.Equal(1, probe.Calls);
        Assert.Single(host.Calls);
    }

    [Fact]
    public void 跟随系统但探测不到时沿用当前深浅()
    {
        var (host, probe) = Setup();
        ShellThemeController.Apply(ThemeKind.PclClassic, dark: true);
        host.Calls.Clear();
        probe.State = SystemThemeState.Unknown;

        ShellThemeController.ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode.System);

        // 探测不到时不能把界面切成随机状态 → 沿用当前的深色。
        Assert.True(ShellThemeController.IsDark);
        Assert.Equal(1, probe.Calls);
        Assert.Single(host.Calls);
    }

    [Fact]
    public void 强制深色不依赖系统探测()
    {
        var (_, probe) = Setup();

        ShellThemeController.ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode.Dark);

        Assert.True(ShellThemeController.IsDark);
        Assert.Equal(0, probe.Calls);   // 策略已定，不需要探测
    }

    [Fact]
    public void 强制浅色不依赖系统探测()
    {
        var (_, probe) = Setup();

        ShellThemeController.ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode.Light);

        Assert.False(ShellThemeController.IsDark);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void 没有挂宿主回调时状态照样变()
    {
        // 无 UI 宿主（单元测试、设计器）时不能抛 —— 状态变化仍要生效。
        ShellThemeController.HostApply = null;

        ShellThemeController.Apply(ThemeKind.Fluent10, dark: true);

        Assert.Equal(ThemeKind.Fluent10, ShellThemeController.CurrentKind);
        Assert.True(ShellThemeController.IsDark);
    }
}
