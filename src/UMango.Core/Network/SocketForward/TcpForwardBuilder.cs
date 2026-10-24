using System.Net;
using UMango.Core.Network.SocketForward;

namespace UMango.Core.Network.SocketForward;

/// <summary>
/// TCP 端口转发的构造器（流式 API）。
///
/// 对应 PCL 的 `PCL.Core/IO/Net/SocketForward/TcpForwardBuilder.cs`（Apache-2.0），
/// 语义一致，但参数校验更严（见 <see cref="TcpForwardConfig.Validate"/>）。
/// </summary>
public sealed class TcpForwardBuilder
{
    private readonly TcpForwardConfig _cfg = new();

    /// <summary>按当前配置构造工作器。</summary>
    public TcpForwardWorker Build()
    {
        var error = _cfg.Validate();
        if (error is not null) throw new ArgumentException(error);
        return new TcpForwardWorker(_cfg);
    }

    /// <summary>本地监听随机端口（0）。</summary>
    public TcpForwardBuilder BindLocalRandom()
    {
        _cfg.LocalHost = IPAddress.Loopback.ToString();
        _cfg.LocalPort = 0;
        return this;
    }

    /// <summary>本地监听指定端口。</summary>
    public TcpForwardBuilder BindLocal(int port)
    {
        if (port is < 0 or > TcpForwardConfig.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"端口超出范围（0-{TcpForwardConfig.MaxPort}）");
        _cfg.LocalHost = IPAddress.Loopback.ToString();
        _cfg.LocalPort = port;
        return this;
    }

    /// <summary>本地监听指定地址与端口。</summary>
    public TcpForwardBuilder BindLocal(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 0 or > TcpForwardConfig.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"端口超出范围（0-{TcpForwardConfig.MaxPort}）");
        _cfg.LocalHost = address.ToString();
        _cfg.LocalPort = port;
        return this;
    }

    /// <summary>设置目标主机与端口。</summary>
    public TcpForwardBuilder SetRemote(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("目标主机不能为空", nameof(host));
        if (port is <= 0 or > TcpForwardConfig.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"端口超出范围（1-{TcpForwardConfig.MaxPort}）");
        _cfg.RemoteHost = host;
        _cfg.RemotePort = port;
        return this;
    }

    /// <summary>设置目标端点。</summary>
    public TcpForwardBuilder SetRemote(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return SetRemote(remote.Address, remote.Port);
    }

    /// <summary>设置目标地址与端口。</summary>
    public TcpForwardBuilder SetRemote(IPAddress host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return SetRemote(host.ToString(), port);
    }

    /// <summary>设置收发缓冲区大小。</summary>
    public TcpForwardBuilder SetBufferSize(uint size)
    {
        if (size > TcpForwardConfig.MaxBufferSize)
            throw new ArgumentOutOfRangeException(nameof(size), size, $"缓冲区不能大于 {TcpForwardConfig.MaxBufferSize}");
        if (size < TcpForwardConfig.MinBufferSize)
            throw new ArgumentOutOfRangeException(nameof(size), size, $"缓冲区不能小于 {TcpForwardConfig.MinBufferSize}");
        _cfg.BufferSize = size;
        return this;
    }

    /// <summary>设置最大并发连接数。</summary>
    public TcpForwardBuilder SetMaxAllowedActiveConnection(ushort maxConnectionCount)
    {
        if (maxConnectionCount == 0) throw new ArgumentOutOfRangeException(nameof(maxConnectionCount), maxConnectionCount, "最大连接数不能为 0");
        _cfg.MaxConnection = maxConnectionCount;
        return this;
    }
}
