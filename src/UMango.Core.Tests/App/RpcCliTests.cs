using UMango.Core.App;
using UMango.Core.Ipc;
using UMango.Core.Ipc.Rpc;
using Xunit;

namespace UMango.Core.Tests.App;

/// <summary>
/// <c>umango rpc …</c> 的行为。
/// </summary>
public sealed class RpcCliTests
{
    [Fact]
    public void 命令行把_rpc_之后的内容全放进位置参数()
    {
        Assert.True(UmangoCommandLine.TryParse(["rpc", "REQ", "ping"], out var line));
        // "rpc" 是**主命令**（args[0]），子命令要靠后面再出现一个已注册的名字；
        // "REQ"/"ping" 都不是已注册的子命令名，所以落在位置参数里。
        Assert.Equal("rpc", line!.CommandText);
        Assert.Null(line.SubcommandText);
        Assert.Equal(["REQ", "ping"], line.Positional);
    }

    [Fact]
    public async Task 缺少动词时返回用法错误()
    {
        var result = await RpcCli.ExecuteAsync([]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("用法"));
    }

    [Fact]
    public async Task 无法识别的动词时报错()
    {
        var result = await RpcCli.ExecuteAsync(["WHAT", "ping"], "umango-no-such-pipe");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("动词"));
    }

    [Fact]
    public async Task 没有运行实例时报错()
    {
        // 指向一个不存在的管道名文件 → 直接报「没有运行中的实例」，
        // 不该傻等连接超时。
        var result = await RpcCli.ExecuteAsync(
            ["REQ", "ping"], null, CancellationToken.None, "/tmp/umango-no-such-dir/rpc.pipe");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("没有正在运行"));
    }

    [Fact]
    public async Task 调用成功时把内容原样带回()
    {
        var pipeName = "umango-cli-" + Guid.NewGuid().ToString("N")[..12];
        await using var service = new RpcService(pipeName);
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));
        service.Registry.AddProperty(new RpcProperty("version", () => "1.2.3"));

        var result = await RpcCli.ExecuteAsync(["REQ", "ping"], pipeName);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["pong"], result.Lines);

        var get = await RpcCli.ExecuteAsync(["GET", "version"], pipeName);
        Assert.Equal(0, get.ExitCode);
        Assert.Equal(["1.2.3"], get.Lines);
    }

    [Fact]
    public async Task 服务端拒绝时退出码非零()
    {
        var pipeName = "umango-cli-" + Guid.NewGuid().ToString("N")[..12];
        await using var service = new RpcService(pipeName);
        // 默认策略：全部拒绝
        service.Registry.AddFunction("secret", (_, _, _) => RpcResponse.Success("不该出现"));

        var result = await RpcCli.ExecuteAsync(["REQ", "secret"], pipeName);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain(result.Lines, l => l.Contains("不该出现"));
    }

    [Fact]
    public async Task 服务端返回_FAILURE_状态时也判为失败()
    {
        var pipeName = "umango-cli-" + Guid.NewGuid().ToString("N")[..12];
        await using var service = new RpcService(pipeName);
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        // RpcResponseStatus.Failure 与 Error 是**两种不同的失败**：
        // 前者是「操作没成功」（如属性写入被拒），后者是「协议/授权问题」。
        // 客户端必须把两者都当失败 —— 把 Failure 当成功是真实会犯的错。
        service.Registry.AddFunction("softfail", (_, _, _) => RpcResponse.EmptyFailure);

        var result = await RpcCli.ExecuteAsync(["REQ", "softfail"], pipeName);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task 函数参数跟在函数名后面()
    {
        var pipeName = "umango-cli-" + Guid.NewGuid().ToString("N")[..12];
        string? seenArgument = null;
        await using var service = new RpcService(pipeName);
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("echo", (argument, _, _) =>
        {
            seenArgument = argument;
            return RpcResponse.Success(argument ?? "(空)");
        });

        var result = await RpcCli.ExecuteAsync(["REQ", "echo", "hello", "world"], pipeName);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello world", seenArgument);
    }
}
