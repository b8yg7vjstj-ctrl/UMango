using System.Net.Sockets;

namespace UMango.Core.Network.SocketForward;

/// <summary>
/// Socket 的温和关闭扩展。
///
/// 对应 PCL 的 `PCL.Core/IO/Net/SocketExtension.cs`（Apache-2.0），但这里只取它的语义
/// （先 Shutdown 再 Dispose，且吞掉关闭过程中的所有异常），
/// 并去掉 PCL 那句多余的 <c>socket.Dispose()</c> 重复路径。
/// </summary>
public static class SocketExtensions
{
    /// <summary>
    /// 温和关闭一个 socket：能 Shutdown 就 Shutdown（让对端收到 FIN），
    /// 然后释放。任何异常都被吞掉 —— 关闭失败时继续持有句柄反而更糟。
    /// </summary>
    public static void CloseGracefully(this Socket? socket)
    {
        if (socket is null) return;

        try
        {
            if (socket.Connected)
            {
                try { socket.Shutdown(SocketShutdown.Both); }
                catch { /* 关闭时的错误不值得关心 */ }
            }
        }
        catch { /* 同上 */ }

        try { socket.Dispose(); }
        catch { /* 同上 */ }
    }
}
