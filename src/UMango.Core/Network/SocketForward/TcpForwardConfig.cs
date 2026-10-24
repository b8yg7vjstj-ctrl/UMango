using System.Net;

namespace UMango.Core.Network.SocketForward;

/// <summary>
/// TCP 端口转发的配置。
///
/// **与 PCL 原版的差异**：端口用 <see cref="int"/> 而不是 <see cref="ushort"/>。
/// 原版在 <c>SetRemote(IPEndPoint)</c> 里写 <c>(ushort) remote.Port</c> ——
/// 端口号超过 65535 时会**静默截断**成一个完全不同的端口，转发会连到错误的目标。
/// 这里直接用 int，并在构造期做范围校验。
/// </summary>
public sealed class TcpForwardConfig
{
    /// <summary>本地监听地址（默认 127.0.0.1）。</summary>
    public string? LocalHost { get; set; }

    /// <summary>本地监听端口（0 = 由系统随机分配）。</summary>
    public int LocalPort { get; set; }

    /// <summary>目标主机名或 IP。</summary>
    public string? RemoteHost { get; set; }

    /// <summary>目标端口。</summary>
    public int RemotePort { get; set; }

    /// <summary>最大并发连接数。</summary>
    public ushort MaxConnection { get; set; } = 10;

    /// <summary>缓冲区上限（32 KB）。</summary>
    public const uint MaxBufferSize = 32 * 1024;

    /// <summary>缓冲区下限（1 KB）。</summary>
    public const uint MinBufferSize = 1024;

    /// <summary>收发缓冲区大小。</summary>
    public uint BufferSize { get; set; } = 8192;

    /// <summary>端口号上限。</summary>
    public const int MaxPort = 65535;

    /// <summary>校验配置是否可用。返回错误原因；<c>null</c> 表示没问题。</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(RemoteHost)) return "未设置目标主机";
        if (RemotePort is <= 0 or > MaxPort) return $"目标端口超出范围（1-{MaxPort}）：{RemotePort}";
        if (LocalPort is < 0 or > MaxPort) return $"本地端口超出范围（0-{MaxPort}）：{LocalPort}";
        if (BufferSize is < MinBufferSize or > MaxBufferSize) return $"缓冲区大小超出范围（{MinBufferSize}-{MaxBufferSize}）";
        if (MaxConnection == 0) return "最大连接数不能为 0";
        return null;
    }
}
