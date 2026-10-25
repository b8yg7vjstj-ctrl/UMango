// SPDX-License-Identifier: Apache-2.0
//
// 提权服务（Elevation）的单元测试。
//
// 设计说明取自 PCL-CE 的 PCL.Core/App/Essentials/PromoteService.cs（Apache-2.0）。

using System.Reflection;
using UMango.Core.Platform;
using UMango.Core.Security;
using Xunit;

namespace UMango.Core.Tests.Security;

/// <summary>提权服务的测试。</summary>
[Collection("静态状态")]
public class ElevationServiceTests
{
    // ---------- 操作注册表 ----------

    [Fact]
    public void 注册操作后可执行()
    {
        var reg = new ElevationOperationRegistry();
        Assert.True(reg.Register("echo", arg => arg ?? "null"));
        Assert.True(reg.IsRegistered("echo"));
        var result = reg.Execute(new ElevationRequest("echo", "hi"));
        Assert.True(result.Ok);
        Assert.Equal("hi", result.Value);
    }

    [Fact]
    public void 重复注册同一操作名返回false()
    {
        var reg = new ElevationOperationRegistry();
        Assert.True(reg.Register("a", _ => "1"));
        Assert.False(reg.Register("a", _ => "2"));
        // 原来的实现仍然有效
        Assert.Equal("1", reg.Execute(new ElevationRequest("a")).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空操作名无法注册(string? name)
    {
        var reg = new ElevationOperationRegistry();
        Assert.False(reg.Register(name!, _ => "x"));
        Assert.Empty(reg.RegisteredNames);
    }

    [Fact]
    public void 未注册的操作返回未找到错误()
    {
        var reg = new ElevationOperationRegistry();
        var result = reg.Execute(new ElevationRequest("missing"));
        Assert.False(result.Ok);
        Assert.Equal(ElevationProtocol.ErrOperationNotFound, result.Error);
    }

    [Fact]
    public void 操作抛异常时返回未处理异常错误()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("boom", _ => throw new InvalidOperationException("x"));
        var result = reg.Execute(new ElevationRequest("boom"));
        Assert.False(result.Ok);
        Assert.Equal(ElevationProtocol.ErrUnhandledException, result.Error);
    }

    [Fact]
    public void 操作返回null时返回空错误()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("nil", _ => null!);
        var result = reg.Execute(new ElevationRequest("nil"));
        Assert.False(result.Ok);
        Assert.Equal(ElevationProtocol.ErrEmpty, result.Error);
    }

    [Fact]
    public void 注销后操作不可用()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("x", _ => "1");
        Assert.True(reg.Unregister("x"));
        Assert.False(reg.IsRegistered("x"));
        Assert.False(reg.Unregister("x"));
    }

    [Fact]
    public void 操作名区分大小写()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("Echo", _ => "1");
        Assert.False(reg.IsRegistered("echo"));
    }

    [Fact]
    public void 已注册操作名按序返回()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("b", _ => "1");
        reg.Register("a", _ => "1");
        Assert.Equal(new[] { "a", "b" }, reg.RegisteredNames);
    }

    // ---------- 请求序列化 ----------

    [Fact]
    public void 请求无参数时只输出操作名()
    {
        Assert.Equal("op", new ElevationRequest("op").ToLine());
        Assert.Equal("op", new ElevationRequest("op", "").ToLine());
    }

    [Fact]
    public void 请求有参数时输出操作名加参数()
    {
        Assert.Equal("op arg", new ElevationRequest("op", "arg").ToLine());
    }

    [Fact]
    public void 请求参数里的换行被压成空格()
    {
        var line = new ElevationRequest("op", "a\r\nb\nc").ToLine();
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("op a b c", line);
    }

    // ---------- 能力判定 ----------

    [Theory]
    [InlineData(PlatformKind.Windows, ElevationMethod.WindowsRunAs)]
    [InlineData(PlatformKind.MacOs, ElevationMethod.MacOsScript)]
    [InlineData(PlatformKind.Linux, ElevationMethod.LinuxPkExec)]
    public void 各平台都有对应的提权方式(PlatformKind platform, ElevationMethod expected)
    {
        var availability = ElevationCapability.Detect(platform);
        Assert.True(availability.Supported);
        Assert.Equal(expected, availability.Method);
    }

    // ---------- 来源校验 ----------

    [Fact]
    public void 相同可执行文件路径通过来源校验()
    {
        Assert.True(ElevationSourceGuard.IsSameProgram("/usr/bin/app", "/usr/bin/app"));
    }

    [Fact]
    public void 不同可执行文件路径不通过来源校验()
    {
        Assert.False(ElevationSourceGuard.IsSameProgram("/usr/bin/other", "/usr/bin/app"));
    }

    [Fact]
    public void 路径为空时不通过来源校验()
    {
        Assert.False(ElevationSourceGuard.IsSameProgram((string?)null, "/usr/bin/app"));
        Assert.False(ElevationSourceGuard.IsSameProgram("/usr/bin/app", ""));
        Assert.False(ElevationSourceGuard.IsSameProgram("  ", "/usr/bin/app"));
    }

    [Fact]
    public void 同一文件的不同写法视为同一程序()
    {
        var dir = Path.GetTempPath();
        var p = Path.Combine(dir, "app.exe");
        Assert.True(ElevationSourceGuard.IsSameProgram(p, Path.Combine(dir, ".", "app.exe")));
    }

    [Fact]
    public void FileInfo重载与字符串重载一致()
    {
        var dir = Path.GetTempPath();
        var p = Path.Combine(dir, "app2.exe");
        Assert.True(ElevationSourceGuard.IsSameProgram(new FileInfo(p), p));
        Assert.False(ElevationSourceGuard.IsSameProgram((FileInfo?)null, p));
    }

    // ---------- 命令构造 ----------

    [Fact]
    public void Windows提权命令使用runas动词且走ShellExecute()
    {
        var launch = ElevationCommandBuilder.WindowsRunAs("C:\\app.exe", ["--flag"]);
        Assert.Equal("C:\\app.exe", launch.Program);
        Assert.True(launch.UseShellExecute);
        Assert.Equal("runas", launch.Verb);
        Assert.Equal(new[] { "--flag" }, launch.Arguments);
    }

    [Fact]
    public void Mac提权命令用osascript并带管理员权限()
    {
        var launch = ElevationCommandBuilder.MacOsScript("/usr/local/app", ["--x"]);
        Assert.Equal("osascript", launch.Program);
        Assert.False(launch.UseShellExecute);
        Assert.Contains("with administrator privileges", launch.Arguments[1]);
    }

    [Fact]
    public void Mac提权命令转义参数里的单引号()
    {
        var launch = ElevationCommandBuilder.MacOsScript("/bin/sh", ["-c", "echo 'hi'"]);
        var script = launch.Arguments[1];
        // AppleScript 字面量里反斜杠被转义过一次，先还原再看 shell 层的转义
        var shellCommand = script.Replace("\\\"", "\"").Replace("\\\\", "\\");
        // 单引号必须被转义成 '\''，不能原样出现在 shell 字符串里
        Assert.Contains("'\\''", shellCommand);
    }

    [Fact]
    public void Mac提权命令转义AppleScript里的双引号()
    {
        var quoted = ElevationCommandBuilder.QuoteAppleScript("a\"b\\c");
        Assert.Equal("\"a\\\"b\\\\c\"", quoted);
    }

    [Fact]
    public void Shell引号转义把单引号换成经典写法()
    {
        Assert.Equal("'a'\\''b'", ElevationCommandBuilder.QuoteShell("a'b"));
        Assert.Equal("'plain'", ElevationCommandBuilder.QuoteShell("plain"));
    }

    [Fact]
    public void Linux提权命令用pkexec()
    {
        var launch = ElevationCommandBuilder.LinuxPkExec("/usr/bin/thing", ["-v"]);
        Assert.Equal("pkexec", launch.Program);
        Assert.Equal(new[] { "/usr/bin/thing", "-v" }, launch.Arguments);
    }

    [Fact]
    public void 按平台构造的命令与各自方法一致()
    {
        var w = ElevationCommandBuilder.Build(PlatformKind.Windows, "a.exe");
        var m = ElevationCommandBuilder.Build(PlatformKind.MacOs, "a");
        var l = ElevationCommandBuilder.Build(PlatformKind.Linux, "a");
        Assert.Equal("runas", w.Verb);
        Assert.Equal("osascript", m.Program);
        Assert.Equal("pkexec", l.Program);
    }

    [Fact]
    public void 空程序名构造命令会抛异常()
    {
        Assert.Throws<ArgumentException>(() => ElevationCommandBuilder.Build(PlatformKind.Windows, "  "));
    }

    [Fact]
    public void 命令行预览给含空格的参数加引号()
    {
        var launch = ElevationCommandBuilder.WindowsRunAs("a.exe", ["with space"]);
        Assert.Equal("a.exe \"with space\"", launch.CommandLine);
    }

    // ---------- 通道 ----------

    [Fact]
    public void 通道未启动时拒绝处理请求()
    {
        var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        Assert.Equal(ElevationChannelState.Idle, ch.State);
        Assert.Equal(ElevationProtocol.ErrChannelUnavailable, ch.HandleLine("op"));
    }

    [Fact]
    public void 通道启动后可处理请求()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("echo", arg => arg ?? "");
        using var ch = new ElevationChannel(reg, "/app");
        ch.Start();
        Assert.Equal(ElevationChannelState.Ready, ch.State);
        Assert.Equal("hi", ch.HandleLine("echo hi"));
        Assert.Equal(1, ch.HandledCount);
    }

    [Fact]
    public void 通道把无参数操作与参数分开()
    {
        var reg = new ElevationOperationRegistry();
        reg.Register("op", arg => arg ?? "<none>");
        using var ch = new ElevationChannel(reg, "/app");
        ch.Start();
        Assert.Equal("<none>", ch.HandleLine("op"));
        Assert.Equal("v", ch.HandleLine("op v"));
    }

    [Fact]
    public void 通道拒绝空行()
    {
        using var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        ch.Start();
        Assert.Equal(ElevationProtocol.ErrIllegalArgument, ch.HandleLine("   "));
        Assert.Equal(ElevationProtocol.ErrIllegalArgument, ch.HandleLine(null));
    }

    [Fact]
    public void 通道返回操作的错误码()
    {
        using var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        ch.Start();
        Assert.Equal(ElevationProtocol.ErrOperationNotFound, ch.HandleLine("nope"));
    }

    [Fact]
    public void 通道来源校验只放行同一程序()
    {
        using var ch = new ElevationChannel(new ElevationOperationRegistry(), "/usr/bin/app");
        Assert.True(ch.IsSourceAllowed("/usr/bin/app"));
        Assert.False(ch.IsSourceAllowed("/usr/bin/other"));
        Assert.False(ch.IsSourceAllowed(null));
    }

    [Fact]
    public void 通道来源校验可叠加额外策略()
    {
        using var ch = new ElevationChannel(
            new ElevationOperationRegistry(), "/usr/bin/app", _ => false);
        Assert.False(ch.IsSourceAllowed("/usr/bin/app"));
    }

    [Fact]
    public void 停止后通道进入已停止状态()
    {
        var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        ch.Start();
        ch.Stop();
        Assert.Equal(ElevationChannelState.Stopped, ch.State);
        Assert.Equal(ElevationProtocol.ErrChannelUnavailable, ch.HandleLine("x"));
    }

    [Fact]
    public void 重复启动不会改变状态()
    {
        using var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        ch.Start();
        ch.Start();
        Assert.Equal(ElevationChannelState.Ready, ch.State);
    }

    [Fact]
    public void 管道名包含进程号且不超长()
    {
        using var ch = new ElevationChannel(new ElevationOperationRegistry(), "/app");
        Assert.Contains(Environment.ProcessId.ToString(), ch.PipeName);
        Assert.True(ch.PipeName.Length < 100, ch.PipeName);
    }

    // ---------- 系统目录判定 ----------

    [Fact]
    public void 非Windows平台不判定特权目录()
    {
        Assert.False(ElevationCommandBuilder.LooksPrivileged("/usr/bin/x", PlatformKind.Linux));
        Assert.False(ElevationCommandBuilder.LooksPrivileged("/usr/bin/x", PlatformKind.MacOs));
    }

    [Fact]
    public void Windows系统目录下的路径判定为特权()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
            return; // 该环境没有 System 目录，跳过
        var exe = Path.Combine(system, "notepad.exe");
        Assert.True(ElevationCommandBuilder.LooksPrivileged(exe, PlatformKind.Windows));
    }

    [Fact]
    public void Windows非系统目录的路径不判定为特权()
    {
        var exe = Path.Combine(Path.GetTempPath(), "plain.exe");
        Assert.False(ElevationCommandBuilder.LooksPrivileged(exe, PlatformKind.Windows));
    }

    [Fact]
    public void 空路径不判定为特权()
    {
        Assert.False(ElevationCommandBuilder.LooksPrivileged(null, PlatformKind.Windows));
        Assert.False(ElevationCommandBuilder.LooksPrivileged("  ", PlatformKind.Windows));
    }

    [Fact]
    public void 协议常量互不相同()
    {
        var codes = new[]
        {
            ElevationProtocol.ErrOperationNotFound,
            ElevationProtocol.ErrIllegalArgument,
            ElevationProtocol.ErrUnhandledException,
            ElevationProtocol.ErrEmpty,
            ElevationProtocol.ErrSourceRejected,
            ElevationProtocol.ErrChannelUnavailable,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
