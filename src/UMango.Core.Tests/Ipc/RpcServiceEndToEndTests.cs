using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Ipc;
using UMango.Core.Ipc.Rpc;
using Xunit;

namespace UMango.Core.Tests.Ipc;

/// <summary>
/// RPC 走真实命名管道的端到端测试。
///
/// 这一组守的是「协议在真实 IO 上能不能跑通」——
/// 纯分发测试（<see cref="RpcRegistryTests"/>）测不到行协议、编码、
/// 管道两端不同步这类问题。
/// </summary>
public class RpcServiceEndToEndTests
{
    private static string UniquePipeName() => "umango-rpc-test-" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<string> SendAsync(string pipeName, string request, string? content = null)
    {
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync(5000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true);
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        // PipeServer 的协议第一行是客户端自报的进程号。
        await writer.WriteLineAsync(Environment.ProcessId.ToString());
        await writer.WriteLineAsync(request);
        if (content is not null)
            await writer.WriteLineAsync(content);
        else
            await writer.WriteLineAsync("");
        await writer.FlushAsync();

        var head = await reader.ReadLineAsync();
        var body = await reader.ReadLineAsync();

        return body is null ? head ?? "" : head + "\n" + body;
    }

    [Fact]
    public async Task 端到端_调用函数能拿到响应()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var response = await SendAsync(pipeName, "REQ ping");

        Assert.Contains("SUCCESS", response);
        Assert.Contains("pong", response);
    }

    [Fact]
    public async Task 端到端_读属性能拿到值()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddProperty(new RpcProperty("version", () => "1.2.3"));

        var response = await SendAsync(pipeName, "GET version");

        Assert.Contains("SUCCESS", response);
        Assert.Contains("1.2.3", response);
    }

    [Fact]
    public async Task 端到端_写属性能成功()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        string? written = null;
        service.Registry.AddProperty(new RpcProperty("theme", () => "light", v => written = v));

        var response = await SendAsync(pipeName, "SET theme", "dark");

        Assert.Contains("SUCCESS", response);
        Assert.Equal("dark", written);
    }

    [Fact]
    public async Task 端到端_默认策略下请求被拒绝()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var response = await SendAsync(pipeName, "REQ ping");

        Assert.Contains("ERROR", response);
        Assert.DoesNotContain("pong", response);
    }

    [Fact]
    public async Task 端到端_不存在的函数返回错误()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;

        var response = await SendAsync(pipeName, "REQ nope");

        Assert.Contains("ERROR", response);
    }

    [Fact]
    public async Task 端到端_协议错误也返回错误而不是断开()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。

        var response = await SendAsync(pipeName, "DELETE version");

        Assert.Contains("ERROR", response);
    }

    [Fact]
    public async Task 端到端_连续两次请求都能被处理()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var first = await SendAsync(pipeName, "REQ ping");
        var second = await SendAsync(pipeName, "REQ ping");

        Assert.Contains("pong", first);
        Assert.Contains("pong", second);
        Assert.True(service.SessionCount >= 2);
    }
}

/// <summary>RPC 客户端的端到端行为。</summary>
public class RpcClientEndToEndTests
{
    private static string UniquePipeName() => "umango-rpcc-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task 客户端调用函数能拿到响应()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var result = await RpcClient.InvokeAsync(pipeName, "ping");

        Assert.True(result.IsSuccess);
        Assert.Equal("pong", result.Content);
    }

    [Fact]
    public async Task 客户端读属性能拿到值()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddProperty(new RpcProperty("version", () => "9.9.9"));

        var result = await RpcClient.GetAsync(pipeName, "version");

        Assert.True(result.IsSuccess);
        Assert.Equal("9.9.9", result.Content);
    }

    [Fact]
    public async Task 客户端写属性能成功()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        string? written = null;
        service.Registry.AddProperty(new RpcProperty("theme", () => "light", v => written = v));

        var result = await RpcClient.SetAsync(pipeName, "theme", "dark");

        Assert.True(result.IsSuccess);
        Assert.Equal("dark", written);
    }

    [Fact]
    public async Task 客户端调用不存在的函数拿到错误()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;

        var result = await RpcClient.InvokeAsync(pipeName, "nope");

        Assert.False(result.IsSuccess);
        Assert.Equal(RpcResponseStatus.Error, result.Status);
    }

    [Fact]
    public async Task 客户端连不上管道时抛超时而不是永远等()
    {
        // 管道名故意用不存在的
        var result = await Assert.ThrowsAnyAsync<Exception>(() =>
            RpcClient.InvokeAsync("umango-no-such-pipe-xyz", "ping", timeout: TimeSpan.FromMilliseconds(300)));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task 客户端的函数参数会被服务端收到()
    {
        var pipeName = UniquePipeName();
        await using var service = new RpcService(pipeName);
        // 用完立刻等服务端真正停下：管道名被释放后下一个用例才能用同名管道。
        // 不等的话会随机撞上"管道已占用"（表现为偶发 1 个失败）。
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        string? got = null;
        service.Registry.AddFunction("echo", (arg, _, _) =>
        {
            got = arg;
            return RpcResponse.Success(arg ?? "");
        });

        await RpcClient.InvokeAsync(pipeName, "echo hello world");

        Assert.Equal("hello world", got);
    }
}

/// <summary>
/// 管道协议的**字节级**约定。
///
/// 这一组守的是一个已经真实踩到过的坑：
/// 服务端用 <c>Encoding.UTF8</c> 写流时，<c>StreamWriter</c> 会在开头
/// 顺手写一个 BOM（U+FEFF），于是响应首行变成 <c>\uFEFFSUCCESS text …</c>。
/// 客户端按空格切字段时，第一个字段是 <c>"\uFEFFSUCCESS"</c> ——
/// 任何按名字解析状态的代码都会失败（表现为「服务端返回成功，客户端说失败」）。
/// </summary>
public class PipeProtocolEncodingTests
{
    [Fact]
    public void 管道编码不带_BOM()
    {
        // Encoding.UTF8 的 encoderShouldEmitUTF8Identifier 是 true —— 不能用它。
        Assert.False(PipeProtocol.Encoding.GetPreamble().Length > 0);
    }

    [Fact]
    public void 管道编码仍然是_UTF8()
    {
        Assert.Equal(65001, PipeProtocol.Encoding.CodePage); // UTF-8
    }

    [Fact]
    public async Task 响应首行不含零宽字符()
    {
        var pipeName = "umango-bom-" + Guid.NewGuid().ToString("N")[..12];
        await using var service = new RpcService(pipeName);
        service.Registry.Authorization = RpcAuthorizationPolicies.AllowAll;
        service.Registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var writer = new StreamWriter(client, PipeProtocol.Encoding, 4096, true);
        using var reader = new StreamReader(client, PipeProtocol.Encoding, detectEncodingFromByteOrderMarks: true, 4096, true);

        await writer.WriteLineAsync("1234");
        await writer.WriteLineAsync("REQ ping");
        await writer.WriteLineAsync("");
        await writer.FlushAsync();

        var head = await reader.ReadLineAsync();

        Assert.NotNull(head);
        Assert.StartsWith("SUCCESS", head);
        Assert.DoesNotContain('﻿', head);
    }
}
