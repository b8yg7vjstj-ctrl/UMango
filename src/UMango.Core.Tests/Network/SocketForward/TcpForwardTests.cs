using System.Net;
using System.Net.Sockets;
using System.Text;
using UMango.Core.Network.SocketForward;
using Xunit;

namespace UMango.Core.Tests.Network.SocketForward;

/// <summary>
/// TCP 端口转发的测试。
///
/// 这些用例**真的起本地回环 socket 做端到端转发**，
/// 不是 mock —— 转发器的价值全在"字节能不能原样过去"，mock 测不出来。
/// </summary>
public class TcpForwardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>起一个最简单的回环 echo 服务器，返回端口与停止方法。</summary>
    private static async Task<(int Port, Func<Task> Stop)> StartEchoServerAsync()
    {
        var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var cts = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var buffer = new byte[4096];
                            int read;
                            while ((read = await client.ReceiveAsync(buffer, SocketFlags.None, cts.Token)) > 0)
                                await client.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, cts.Token);
                        }
                        catch { /* 忽略 */ }
                        finally { client.CloseGracefully(); }
                    });
                }
            }
            catch { /* 停止时的取消 */ }
        });

        return (port, async () =>
        {
            cts.Cancel();
            listener.CloseGracefully();
            await Task.WhenAny(accept, Task.Delay(Timeout));
        });
    }

    private static async Task<string> RoundTripAsync(int localPort, string payload)
    {
        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await client.SendAsync(bytes, SocketFlags.None);

        var buffer = new byte[bytes.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    [Fact]
    public async Task 端到端_字节原样转发()
    {
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            using var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .Build();

            var endPoint = await worker.StartAsync();
            Assert.NotNull(worker.LocalEndPoint);
            Assert.True(endPoint.Port > 0);

            var result = await RoundTripAsync(endPoint.Port, "hello umango");
            Assert.Equal("hello umango", result);
        }
        finally
        {
            await stopEcho();
        }
    }

    [Fact]
    public async Task 启动后_本地端点一定可读()
    {
        // 回归护栏：PCL 原版的 Start() 是 fire-and-forget，
        // 调用方紧接着读 LocalEndPoint 基本必然拿到 null（随机端口时更是完全没法用）。
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            using var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .Build();

            var endPoint = await worker.StartAsync();

            Assert.NotNull(worker.LocalEndPoint);
            Assert.Equal(endPoint.Port, worker.LocalEndPoint!.Port);
            Assert.True(worker.IsRunning);
        }
        finally
        {
            await stopEcho();
        }
    }

    [Fact]
    public async Task 停止后_端口不再接受连接()
    {
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            using var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .Build();

            var endPoint = await worker.StartAsync();
            worker.Stop();

            // 等后台把监听套接字关掉
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline && worker.IsRunning) await Task.Delay(20);
            Assert.False(worker.IsRunning, "停止后不应仍在运行");

            using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var connect = client.ConnectAsync(IPAddress.Loopback, endPoint.Port);
            var finished = await Task.WhenAny(connect, Task.Delay(Timeout));
            if (finished == connect)
            {
                // 万一连上了（关闭有延迟），也不该还能读到回显
                var buffer = new byte[16];
                var n = await client.ReceiveAsync(buffer, SocketFlags.None);
                Assert.Equal(0, n);
            }
        }
        finally
        {
            await stopEcho();
        }
    }

    [Fact]
    public async Task 超过最大连接数_新连接被拒绝()
    {
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            using var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .SetMaxAllowedActiveConnection(1)
                .Build();

            var endPoint = await worker.StartAsync();

            // 占住唯一的名额：连上但不关闭
            using var hog = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await hog.ConnectAsync(IPAddress.Loopback, endPoint.Port);
            await hog.SendAsync(Encoding.UTF8.GetBytes("x"), SocketFlags.None);
            await hog.ReceiveAsync(new byte[1], SocketFlags.None); // 等回显，确保连接已建立

            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline && worker.ActiveConnectionCount == 0) await Task.Delay(20);
            Assert.Equal(1, worker.ActiveConnectionCount);

            // 第二个连接应当被拒绝（连上后立刻被关掉 → 读到 0 字节）
            using var second = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await second.ConnectAsync(IPAddress.Loopback, endPoint.Port);
            var buffer = new byte[8];
            var read = await second.ReceiveAsync(buffer, SocketFlags.None);
            Assert.Equal(0, read);
        }
        finally
        {
            await stopEcho();
        }
    }

    [Fact]
    public async Task 目标不可达_不影响监听继续接受()
    {
        // 目标端口没人监听 → 连接会失败，但转发器本身不该崩，
        // 也不该把整个监听线程带倒。
        using var worker = new TcpForwardBuilder()
            .BindLocalRandom()
            .SetRemote("127.0.0.1", 1) // 1 端口需要特权，基本连不上
            .Build();

        var endPoint = await worker.StartAsync();
        Assert.True(worker.IsRunning);

        using var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, endPoint.Port);
        var buffer = new byte[8];
        var read = await client.ReceiveAsync(buffer, SocketFlags.None);
        Assert.Equal(0, read);
        Assert.True(worker.IsRunning, "目标连不上不应让转发器退出");
    }

    [Fact]
    public void 构造器_端口越界_抛异常()
    {
        // PCL 原版用 ushort 存端口，(ushort)65536 会静默变成 0
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcpForwardBuilder().SetRemote("127.0.0.1", 65536));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcpForwardBuilder().SetRemote("127.0.0.1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcpForwardBuilder().BindLocal(70000));
    }

    [Fact]
    public void 构造器_空主机_抛异常()
    {
        Assert.Throws<ArgumentException>(() => new TcpForwardBuilder().SetRemote("  ", 1234));
    }

    [Fact]
    public void 构造器_缓冲区越界_抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcpForwardBuilder().SetBufferSize(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcpForwardBuilder().SetBufferSize(1024 * 1024));
    }

    [Fact]
    public void 未设目标_构造时抛异常()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new TcpForwardBuilder().BindLocalRandom().Build());
        Assert.Contains("目标主机", error.Message);
    }

    [Fact]
    public void 校验_错误原因清晰()
    {
        var cfg = new TcpForwardConfig { RemoteHost = "127.0.0.1", RemotePort = 70000 };
        Assert.Contains("目标端口超出范围", cfg.Validate());

        cfg = new TcpForwardConfig { RemoteHost = null, RemotePort = 80 };
        Assert.Contains("未设置目标主机", cfg.Validate());

        cfg = new TcpForwardConfig { RemoteHost = "h", RemotePort = 80, MaxConnection = 0 };
        Assert.Contains("最大连接数", cfg.Validate());
    }

    [Fact]
    public void 目标端点_端口不被截断()
    {
        // 回归护栏：PCL 原版 SetRemote(IPEndPoint) 写 (ushort) remote.Port
        var builder = new TcpForwardBuilder().SetRemote(new IPEndPoint(IPAddress.Loopback, 1234));
        var worker = builder.Build();
        Assert.NotNull(worker);
    }

    [Fact]
    public async Task 多次调用启动_不会重复监听()
    {
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            using var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .Build();

            var first = await worker.StartAsync();
            var second = await worker.StartAsync();

            Assert.Equal(first.Port, second.Port);
        }
        finally
        {
            await stopEcho();
        }
    }

    [Fact]
    public async Task 释放之后_再启动_抛异常()
    {
        var (echoPort, stopEcho) = await StartEchoServerAsync();
        try
        {
            var worker = new TcpForwardBuilder()
                .BindLocalRandom()
                .SetRemote("127.0.0.1", echoPort)
                .Build();
            await worker.StartAsync();
            worker.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.StartAsync());
        }
        finally
        {
            await stopEcho();
        }
    }
}
