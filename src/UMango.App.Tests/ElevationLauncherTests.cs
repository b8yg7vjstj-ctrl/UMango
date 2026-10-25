// SPDX-License-Identifier: Apache-2.0
//
// 提权启动器（App 层）的单元测试。
//
// 关键点：**绝不真的弹 UAC 对话框** —— 用假的 IProcessStarter 断言
// 「发起了什么命令」，这正是把命令构造放进 Core 的理由。

using UMango.App.Services;
using UMango.Core.Security;
using Xunit;

namespace UMango.App.Tests;

/// <summary>记录发起过的命令的假启动器。</summary>
internal sealed class RecordingProcessStarter : IProcessStarter
{
    /// <summary>发起过的命令。</summary>
    public List<ElevationLaunch> Launches { get; } = [];

    /// <summary>要返回的进程 id（null = 启动失败）。</summary>
    public int? Result { get; set; } = 4242;

    /// <inheritdoc />
    public int? Start(ElevationLaunch launch)
    {
        Launches.Add(launch);
        return Result;
    }
}

[Collection("静态状态")]
public class ElevationLauncherTests
{
    [Fact]
    public void 启动成功返回进程号()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new ElevationLauncher(starter);
        var result = launcher.Launch("/bin/echo", ["hi"]);
        Assert.True(result.Succeeded);
        Assert.Equal(4242, result.ProcessId);
        Assert.Single(starter.Launches);
    }

    [Fact]
    public void 启动失败返回原因()
    {
        var starter = new RecordingProcessStarter { Result = null };
        var launcher = new ElevationLauncher(starter);
        var result = launcher.Launch("/bin/echo");
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Reason);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void 空程序名不发起进程()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new ElevationLauncher(starter);
        var result = launcher.Launch("   ");
        Assert.False(result.Succeeded);
        Assert.Empty(starter.Launches);
    }

    [Fact]
    public void 启动自身使用当前程序路径()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new ElevationLauncher(starter);
        var result = launcher.LaunchSelf(["--x"]);
        Assert.True(result.Succeeded);
        Assert.Single(starter.Launches);
        var launch = starter.Launches[0];
        // Windows 上 Program 就是自身路径；macOS/Linux 上 Program 是 osascript/pkexec，
        // 自身路径出现在参数里。
        var all = launch.Program + " " + string.Join(' ', launch.Arguments);
        Assert.Contains(Environment.ProcessPath!, all);
    }

    [Fact]
    public void 命令按当前平台构造()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new ElevationLauncher(starter);
        launcher.Launch("app", ["--k", "v"]);
        var launch = starter.Launches[0];
        switch (UMango.Core.Platform.SystemInfo.Platform)
        {
            case UMango.Core.Platform.PlatformKind.Windows:
                Assert.Equal("runas", launch.Verb);
                Assert.True(launch.UseShellExecute);
                break;
            case UMango.Core.Platform.PlatformKind.MacOs:
                Assert.Equal("osascript", launch.Program);
                break;
            default:
                Assert.Equal("pkexec", launch.Program);
                break;
        }
    }

    [Fact]
    public void 平台不支持提权时不发起进程()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new ElevationLauncher(
            starter,
            () => ElevationAvailability.NotSupported("测试用：不支持"));
        var result = launcher.Launch("/bin/echo");
        Assert.False(result.Succeeded);
        Assert.Equal("测试用：不支持", result.Reason);
        Assert.Empty(starter.Launches);
    }

    [Fact]
    public void 能力判定与核心一致()
    {
        var launcher = new ElevationLauncher(new RecordingProcessStarter());
        Assert.Equal(ElevationCapability.Detect().Method, launcher.Availability.Method);
    }
}
