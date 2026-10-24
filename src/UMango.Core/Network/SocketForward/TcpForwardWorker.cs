using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UMango.Core.Logging;

namespace UMango.Core.Network.SocketForward;

/// <summary>
/// TCP 端口转发工作器：把本地监听到的连接原样转发到远端目标。
///
/// 对应 PCL 的 `PCL.Core/IO/Net/SocketForward/TcpForwardWorker.cs`（Apache-2.0）。
/// 这里重新实现，保留它的整体结构（监听 → 接受 → 双向泵数据 → 关闭），
/// 并修掉几个原版的真实缺陷（见下）。
/// </summary>
public sealed class TcpForwardWorker : IDisposable
{
    private const string ModuleName = "TcpForward";

    private readonly TcpForwardConfig _cfg;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly ConcurrentDictionary<Guid, Task> _subWorkerTasks = [];
    private readonly Lock _operationLock = new();

    private CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private TaskCompletionSource<IPEndPoint>? _started;
    private bool _disposed;

    /// <summary>构造。配置非法时抛 <see cref="ArgumentException"/>。</summary>
    public TcpForwardWorker(TcpForwardConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var error = cfg.Validate();
        if (error is not null) throw new ArgumentException(error, nameof(cfg));

        _cfg = cfg;
        _connectionSemaphore = new SemaphoreSlim(cfg.MaxConnection, cfg.MaxConnection);
    }

    /// <summary>实际监听的端点。<c>StartAsync</c> 成功之后才有值。</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>当前活跃连接数。</summary>
    public int ActiveConnectionCount => _cfg.MaxConnection - _connectionSemaphore.CurrentCount;

    /// <summary>是否正在运行。</summary>
    public bool IsRunning => _workerTask is { IsCompleted: false };

    /// <summary>
    /// 启动转发并**等待监听就绪**。
    ///
    /// **与 PCL 原版的差异**：PCL 的 <c>Start()</c> 是 fire-and-forget —— 它启动后台任务就立刻返回，
    /// 而 <c>LocalEndPoint</c> 要等到后台任务跑到 <c>listener.Bind()</c> 之后才被赋值。
    /// 于是调用方几乎必然在 <c>Start()</c> 之后立刻读到 <c>null</c>（本地端口随机分配时更是完全没法用）。
    /// 这里让启动可以 await，<see cref="LocalEndPoint"/> 一定有值。
    /// </summary>
    public async Task<IPEndPoint> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TaskCompletionSource<IPEndPoint> started;
        lock (_operationLock)
        {
            if (_workerTask is { IsCompleted: false } && LocalEndPoint is not null)
                return LocalEndPoint;

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            started = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _started = started;
            LocalEndPoint = null;
            _workerTask = RunWorkerAsync(_cts.Token, started);
        }

        await using var registration = cancellationToken.Register(() => started.TrySetCanceled(cancellationToken));
        return await started.Task.ConfigureAwait(false);
    }

    /// <summary>启动转发（阻塞等待就绪）。</summary>
    public IPEndPoint Start(CancellationToken cancellationToken = default)
        => StartAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>停止转发。已经在跑的连接会在后台陆续关闭。</summary>
    public void Stop()
    {
        CancellationTokenSource cts;
        Task worker;
        Task[] subs;
        lock (_operationLock)
        {
            if (_workerTask is not { IsCompleted: false } && _subWorkerTasks.IsEmpty) return;
            cts = _cts;
            worker = _workerTask ?? Task.CompletedTask;
            subs = [.. _subWorkerTasks.Values];
            _subWorkerTasks.Clear();
        }

        cts.Cancel();

        // 后台等所有转发线程收尾再释放信号量 —— 否则运行中的连接结束时
        // Release() 会打到已释放的信号量上（PCL 原版靠 catch ObjectDisposedException 掩盖）。
        _ = Task.WhenAll([worker, .. subs]).ContinueWith(t =>
        {
            try { cts.Dispose(); }
            catch { /* 忽略 */ }
            if (t.IsFaulted) Log.Error(ModuleName, t.Exception!, "停止 TCP 端口转发时后台线程出错");
        });

        Log.Info(ModuleName, "TCP 端口转发已停止，转发线程将在后台陆续关闭");
    }

    /// <summary>等待所有已建立的连接结束（测试与优雅退出用）。</summary>
    public async Task WaitForConnectionsAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_operationLock) pending = [.. _subWorkerTasks.Values];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken, TaskCompletionSource<IPEndPoint> started)
    {
        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.NoDelay = true;
            listener.ReceiveBufferSize = (int)_cfg.BufferSize;
            listener.SendBufferSize = (int)_cfg.BufferSize;

            if (!IPAddress.TryParse(_cfg.LocalHost, out var localAddress))
                throw new InvalidOperationException($"本地监听地址无法解析：{_cfg.LocalHost}");

            listener.Bind(new IPEndPoint(localAddress, _cfg.LocalPort));
            listener.Listen();

            if (listener.LocalEndPoint is not IPEndPoint endPoint)
                throw new InvalidCastException("监听套接字未返回预期的端点信息");

            LocalEndPoint = endPoint;
            started.TrySetResult(endPoint);

            Log.Info(ModuleName, $"TCP 端口转发已启动，监听 {endPoint}，目标 tcp://{_cfg.RemoteHost}:{_cfg.RemotePort}");

            while (!cancellationToken.IsCancellationRequested)
            {
                Socket clientSocket;
                try
                {
                    clientSocket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // 监听套接字已被释放 → 收工。
                    // 正常路径走不到这里（监听套接字是 using 的，循环退出才释放）；
                    // PCL 原版把这里当普通错误，会打一条 error 再空转 500ms 继续重试。
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ModuleName, ex, "接受连接时发生错误");
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await _connectionSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    clientSocket.CloseGracefully();
                    Log.Warn(ModuleName, $"已达到最大连接数限制（{_cfg.MaxConnection}），拒绝新连接");
                    continue;
                }

                var id = Guid.NewGuid();
                var task = HandleConnectionAsync(clientSocket, cancellationToken)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted) Log.Error(ModuleName, t.Exception!, "连接处理线程出现错误");
                        _subWorkerTasks.TryRemove(id, out _);
                        try { _connectionSemaphore.Release(); }
                        catch (ObjectDisposedException) { /* 已释放，忽略 */ }
                    }, TaskScheduler.Default);

                _subWorkerTasks[id] = task;
            }
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            Log.Error(ModuleName, ex, "TCP 端口转发工作线程出现错误");
        }
        finally
        {
            started.TrySetException(new InvalidOperationException("TCP 端口转发在就绪之前结束"));
        }
    }

    private async Task HandleConnectionAsync(Socket clientSocket, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Socket? targetSocket = null;

        try
        {
            Log.Info(ModuleName, $"接受来自 {clientSocket.RemoteEndPoint} 的连接");

            targetSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            targetSocket.NoDelay = true;
            targetSocket.ReceiveBufferSize = (int)_cfg.BufferSize;
            targetSocket.SendBufferSize = (int)_cfg.BufferSize;

            await targetSocket.ConnectAsync(_cfg.RemoteHost!, _cfg.RemotePort, cancellationToken).ConfigureAwait(false);

            var forward1 = ForwardDataAsync(clientSocket, targetSocket, _cfg.BufferSize, cts.Token);
            var forward2 = ForwardDataAsync(targetSocket, clientSocket, _cfg.BufferSize, cts.Token);

            // 任一方向结束就收工（正常的半关闭语义）
            await Task.WhenAny(forward1, forward2).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            Log.Error(ModuleName, ex, "处理连接时发生错误");
        }
        finally
        {
            clientSocket.CloseGracefully();
            targetSocket.CloseGracefully();
        }
    }

    private static async Task ForwardDataAsync(Socket source, Socket destination, uint bufferSize, CancellationToken cancellationToken)
    {
        using var bufferOwner = MemoryPool<byte>.Shared.Rent((int)bufferSize);
        var buffer = bufferOwner.Memory;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await source.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                var bytesSend = 0;
                while (bytesRead > bytesSend)
                {
                    var current = await destination.SendAsync(buffer[bytesSend..bytesRead], SocketFlags.None, cancellationToken)
                        .ConfigureAwait(false);
                    if (current == 0) break;
                    bytesSend += current;
                }

                if (bytesRead != bytesSend) break;
            }
        }
        catch
        {
            // 转发期间的错误一律忽略（连接多半是被对端或本方关掉的）
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        // 信号量的释放交给后台收尾线程 —— 这里不释放，避免运行中的连接结束时 Release 打到已释放对象上
        GC.SuppressFinalize(this);
    }
}
