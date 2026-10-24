// SPDX-License-Identifier: Apache-2.0
//
// 本文件的管道服务端语义参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/IO/PipeComm.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.Ipc；
//   2. **去掉 Windows 专属的客户端进程号校验**（原版用
//      `KernelInterop.GetNamedPipeClientProcessId` 读管道句柄，是 Win32 API）。
//      UMango 要跨平台，这里改成**由客户端在协议里自报进程号** ——
//      自报可以伪造，所以这里只把它当作「日志信息」与「弱校验」，
//      真正需要安全性的场景要用管道 ACL 或别的鉴权手段；
//   3. 原版的服务端跑在一个专用线程上（`Basics.RunInNewThread`），
//      这里改成 **async**，避免占用线程，也更容易与 .NET 的取消机制配合；
//   4. 原版有「写终止符 ESC (char)27」的私有协议，这里改成**行协议**
//      （一行一条消息，UTF-8 编码）—— 可读、可调试、跨实现兼容；
//   5. 原版没有「管道名长度」的概念，但 .NET 在 macOS/Linux 上把命名管道
//      映射成 Unix domain socket，**完整路径长度必须 ≤ 104 字符**，
//      超了会抛 ArgumentOutOfRangeException。本文件为此提供了
//      <see cref="BuildPipeName"/>，自动生成短名并做哈希兜底。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Ipc;

/// <summary>管道通信的常量。</summary>
public static class PipeProtocol
{
    /// <summary>统一字符编码。</summary>
    public static readonly Encoding Encoding = Encoding.UTF8;

    /// <summary>行分隔符（行协议）。</summary>
    public const char LineEnding = '\n';

    /// <summary>
    /// Unix domain socket 的路径长度上限。
    /// .NET 在 macOS/Linux 上把命名管道映射成 `/tmp/…/CoreFxPipe_&lt;name&gt;`，
    /// 超出会抛 <c>ArgumentOutOfRangeException</c>。
    /// </summary>
    public const int MaxPipeNameLength = 64;
}

/// <summary>一次管道会话的上下文。</summary>
public sealed class PipeSession
{
    /// <summary>创建会话。</summary>
    /// <param name="reader">读取端。</param>
    /// <param name="writer">写入端。</param>
    /// <param name="clientProcessId">客户端自报的进程号（可能不可信）。</param>
    public PipeSession(StreamReader reader, StreamWriter writer, int clientProcessId)
    {
        Reader = reader;
        Writer = writer;
        ClientProcessId = clientProcessId;
    }

    /// <summary>读取端。</summary>
    public StreamReader Reader { get; }

    /// <summary>写入端。</summary>
    public StreamWriter Writer { get; }

    /// <summary>客户端自报的进程号。<b>不可信</b>，只作日志与弱校验用。</summary>
    public int ClientProcessId { get; }

    /// <summary>写一行并刷新。</summary>
    public Task WriteLineAsync(string line, CancellationToken ct = default)
    {
        Writer.Write(line);
        Writer.Write(PipeProtocol.LineEnding);
        return Writer.FlushAsync(ct);
    }
}

/// <summary>管道服务端的处理结果。</summary>
public enum PipeSessionResult
{
    /// <summary>继续等待下一个连接。</summary>
    Continue = 0,

    /// <summary>停止服务端。</summary>
    Stop = 1,
}

/// <summary>
/// 命名管道服务端。
///
/// 用途：让**已经在运行的 UMango 实例**接收外部请求 ——
/// 例如命令行 `umango ipc install npm xxx` 把请求转给前台实例，
/// 或让别的工具查询 UMango 的状态。
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _log = [];
    private readonly object _logLock = new();
    private int _sessionCount;

    /// <summary>创建服务端。</summary>
    /// <param name="pipeName">管道名（应为短名，见 <see cref="PipeProtocol.MaxPipeNameLength"/>）。</param>
    /// <param name="handleSession">处理一次会话；返回 <see cref="PipeSessionResult.Stop"/> 可停止服务端。</param>
    /// <param name="onError">出错时的回调（可选）。</param>
    public PipeServer(string pipeName, Func<PipeSession, CancellationToken, Task<PipeSessionResult>> handleSession, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(handleSession);
        PipeName = pipeName;
        _handleSession = handleSession;
        _onError = onError;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private readonly Func<PipeSession, CancellationToken, Task<PipeSessionResult>> _handleSession;
    private readonly Action<Exception>? _onError;

    /// <summary>管道名。</summary>
    public string PipeName { get; }

    /// <summary>已处理的会话数。</summary>
    public int SessionCount => Volatile.Read(ref _sessionCount);

    /// <summary>运行期日志（最近若干条）。</summary>
    public IReadOnlyList<string> RecentLog
    {
        get { lock (_logLock) { return _log.ToArray(); } }
    }

    /// <summary>
    /// 生成一个跨平台安全的短管道名。
    ///
    /// .NET 在 macOS/Linux 上把命名管道映射成 Unix domain socket，
    /// 完整路径长度必须 ≤ 104 字符，而管道名只是其中一部分 ——
    /// 所以这里限制名字本身的长度，超长时用哈希截断。
    /// </summary>
    /// <param name="prefix">前缀（例如 "umango"）。</param>
    /// <param name="discriminator">区分实例的判别串（一般是路径或 id）。</param>
    public static string BuildPipeName(string prefix, string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var raw = prefix + "-" + discriminator;

        if (raw.Length <= PipeProtocol.MaxPipeNameLength)
            return raw;

        // 超长：保留前缀 + 哈希，保证唯一性同时压到长度上限内。
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(discriminator)))[..16];
        var keep = Math.Max(1, PipeProtocol.MaxPipeNameLength - prefix.Length - hash.Length - 1);
        var head = raw[..Math.Min(raw.Length, keep)];
        return head + "-" + hash;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _sessionCount);

                // **只创建一个 reader**：StreamReader 会预读，
                // 如果先用它读一行进程号、再拿它去读请求行，
                // 预读进来的后续行就被第一个 reader 吞掉了 —— 表现为「服务端收不到请求」。
                using var reader = new StreamReader(pipe, PipeProtocol.Encoding, false, 4096, true);
                using var writer = new StreamWriter(pipe, PipeProtocol.Encoding, 4096, true);

                var clientPid = await ReadClientProcessIdAsync(reader, ct).ConfigureAwait(false);
                Log($"已连接（客户端自报进程 {clientPid}）");

                var session = new PipeSession(reader, writer, clientPid);

                var result = await _handleSession(session, ct).ConfigureAwait(false);
                if (result == PipeSessionResult.Stop)
                {
                    Log("处理程序要求停止服务端");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
                Log($"会话出错：{ex.Message}");
            }
            finally
            {
                try
                {
                    pipe?.Dispose();
                }
                catch
                {
                    // 忽略关闭时的异常
                }
            }
        }

        Log("服务端已停止");
    }

    /// <summary>
    /// 读客户端自报的进程号。
    ///
    /// 协议的第一行是进程号（十进制）。读不到就当作 0（未知），
    /// 不因为这一行格式不对就拒绝整个会话 —— 后面的行才是真正的请求。
    /// </summary>
    private static async Task<int> ReadClientProcessIdAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            return int.TryParse(line, out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_log.Count > 50)
                _log.RemoveRange(0, _log.Count - 50);
        }
    }

    /// <summary>停止服务端并释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // 取消导致的异常不算问题
        }
        _cts.Dispose();
    }
}

/// <summary>管道客户端。</summary>
public static class PipeClient
{
    /// <summary>
    /// 向管道服务端发送若干行，并读回响应行。
    /// </summary>
    /// <param name="pipeName">管道名。</param>
    /// <param name="lines">要发送的行（不含行尾符）。</param>
    /// <param name="timeoutMs">连接与读取的总超时。</param>
    /// <returns>读到的响应行；失败时返回 null。</returns>
    public static async Task<IReadOnlyList<string>?> SendAsync(
        string pipeName,
        IReadOnlyList<string> lines,
        int timeoutMs = 5000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

            // 第一行是自报进程号
            await using var writer = new StreamWriter(pipe, PipeProtocol.Encoding, 4096, true) { AutoFlush = true };
            await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            foreach (var line in lines)
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);

            using var reader = new StreamReader(pipe, PipeProtocol.Encoding, false, 4096, true);
            var responses = new List<string>();
            while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
            {
                responses.Add(line);
                if (responses.Count > 64)
                    break;
            }
            return responses;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>进程号（供协议自报使用）。</summary>
    public static int CurrentProcessId => Environment.ProcessId;

    /// <summary>当前进程信息（仅用于日志）。</summary>
    public static string CurrentProcessName
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
