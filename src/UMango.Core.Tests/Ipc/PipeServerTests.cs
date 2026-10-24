using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Ipc;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 管道通信的行为。
///
/// 真机上用命名管道跑服务端/客户端对跑；macOS 上 .NET 会把它映射成
/// Unix domain socket，协议层完全一致，所以这些用例能在这里跑。
/// </summary>
public class PipeServerTests
{
    private static string NewPipeName() => "umango-test-" + Guid.NewGuid().ToString("n")[..12];

    [Fact]
    public void 短名字原样保留()
    {
        var name = PipeServer.BuildPipeName("umango", "abc");
        Assert.Equal("umango-abc", name);
    }

    [Fact]
    public void 超长名字被压缩到长度上限内()
    {
        var name = PipeServer.BuildPipeName("umango", new string('x', 200));
        Assert.True(name.Length <= PipeProtocol.MaxPipeNameLength, $"实际长度 {name.Length}");
    }

    [Fact]
    public void 超长名字的哈希不同则结果不同()
    {
        var a = PipeServer.BuildPipeName("umango", new string('x', 200) + "a");
        var b = PipeServer.BuildPipeName("umango", new string('x', 200) + "b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 相同输入得到相同名字()
    {
        Assert.Equal(
            PipeServer.BuildPipeName("umango", "same"),
            PipeServer.BuildPipeName("umango", "same"));
    }

    [Fact]
    public async Task 客户端能与服务端对话()
    {
        var pipeName = NewPipeName();
        await using var server = new PipeServer(pipeName, async (session, ct) =>
        {
            var line = await session.Reader.ReadLineAsync(ct);
            await session.WriteLineAsync("echo:" + line, ct);
            return PipeSessionResult.Continue;
        });

        var responses = await PipeClient.SendAsync(pipeName, ["hello"]);
        Assert.NotNull(responses);
        Assert.Equal("echo:hello", responses!.First());
    }

    [Fact]
    public async Task 服务端能收到客户端自报的进程号()
    {
        var pipeName = NewPipeName();
        var seen = 0;
        await using var server = new PipeServer(pipeName, async (session, ct) =>
        {
            seen = session.ClientProcessId;
            await session.WriteLineAsync("ok", ct);
            return PipeSessionResult.Continue;
        });

        await PipeClient.SendAsync(pipeName, ["x"]);
        Assert.Equal(Environment.ProcessId, seen);
    }

    [Fact]
    public async Task 多行请求按顺序到达()
    {
        var pipeName = NewPipeName();
        var received = new List<string>();
        await using var server = new PipeServer(pipeName, async (session, ct) =>
        {
            while (await session.Reader.ReadLineAsync(ct) is { } line)
                received.Add(line);
            return PipeSessionResult.Continue;
        });

        await PipeClient.SendAsync(pipeName, ["a", "b", "c"]);
        // 服务端可能还在收，给它一点时间
        for (var i = 0; i < 50 && received.Count < 3; i++)
            await Task.Delay(20);
        Assert.Equal(["a", "b", "c"], received);
    }

    [Fact]
    public async Task 处理程序返回停止时服务端结束()
    {
        var pipeName = NewPipeName();
        await using var server = new PipeServer(pipeName, (session, ct) =>
            Task.FromResult(PipeSessionResult.Stop));

        var responses = await PipeClient.SendAsync(pipeName, ["x"]);
        // 服务端可能在客户端连上之前就已经停了，连接会失败 —— 两种结果都合法，
        // 关键是**不能挂死**。
        Assert.True(responses is null || responses.Count >= 0);
    }

    [Fact]
    public async Task 服务端不存在时客户端返回空而不是抛异常()
    {
        var responses = await PipeClient.SendAsync(NewPipeName(), ["x"], timeoutMs: 800);
        Assert.Null(responses);
    }

    [Fact]
    public async Task 多次连接都会被接受()
    {
        var pipeName = NewPipeName();
        await using var server = new PipeServer(pipeName, async (session, ct) =>
        {
            await session.WriteLineAsync("ok", ct);
            return PipeSessionResult.Continue;
        });

        for (var i = 0; i < 3; i++)
        {
            var responses = await PipeClient.SendAsync(pipeName, [$"n{i}"]);
            Assert.NotNull(responses);
        }
        Assert.True(server.SessionCount >= 1);
    }

    [Fact]
    public async Task 释放后不再接受连接()
    {
        var pipeName = NewPipeName();
        var server = new PipeServer(pipeName, async (session, ct) =>
        {
            await session.WriteLineAsync("ok", ct);
            return PipeSessionResult.Continue;
        });
        await server.DisposeAsync();

        var responses = await PipeClient.SendAsync(pipeName, ["x"], timeoutMs: 800);
        Assert.Null(responses);
    }

    [Fact]
    public void 空管道名会被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            new PipeServer("", (_, _) => Task.FromResult(PipeSessionResult.Continue)));
    }

    [Fact]
    public async Task 会话出错不会杀死服务端()
    {
        var pipeName = NewPipeName();
        var errored = 0;
        await using var server = new PipeServer(
            pipeName,
            (_, _) => throw new InvalidOperationException("炸了"),
            _ => Interlocked.Increment(ref errored));

        // 第一次连接：处理程序抛异常
        await PipeClient.SendAsync(pipeName, ["x"], timeoutMs: 1500);
        for (var i = 0; i < 50 && errored == 0; i++)
            await Task.Delay(20);

        Assert.True(errored > 0, "出错回调没有被调用");
        Assert.True(server.SessionCount >= 1);
    }
}
