using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.SingleInstance;

/// <summary>
/// 单实例保护 + 跨进程激活通道。
///
/// **思路来源**：PCL-CE <c>PCL.Core/App/Essentials/SingleInstanceService.cs</c>（Apache-2.0），
/// 其三个机制：① 单例锁（第二个实例打开即失败）② 命名管道把「activate」等指令
/// 传给已在运行的实例 ③ 由第一个实例负责激活主窗口。
/// 这里是 UMango 自有实现（PCL 依赖其 IoC 生命周期与 RpcService，不整体搬运）。
///
/// 为什么需要：用户重复双击 EXE（或从不同入口启动）会产生第二个进程，
/// 表现为「两个窗口 / 两份日志 / 两套探测」—— 本仓库真机日志里实测出现过
/// 500ms 内两次 <c>UMango 启动</c> 的记录（用户双击过快）。
/// </summary>
public sealed class SingleInstanceGuard : IAsyncDisposable
{
    /// <summary>
    /// 单例互斥的全局名前。
    ///
    /// ⚠ 前缀必须是 <c>Global\</c>：单实例要跨**用户会话**生效（同一台机器上另一个
    /// 账户再开一份也应被拦下），而默认的 <c>Local\</c> 只在当前会话可见。
    /// 名字按锁文件路径取哈希，避免不同安装位置（例：开发机与正式机）互相干扰。
    /// </summary>
    private static string MutexName(string lockFilePath) =>
        "Global\\umango-single-instance-" + Math.Abs(lockFilePath.GetHashCode()).ToString("x8");

    private readonly string _lockFilePath;
    private readonly string _pipeName;
    private readonly Func<string, Task> _onMessage;

    private Mutex? _mutex;
    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource _cts = new();
    private bool _ownsLock;

    /// <param name="lockFilePath">单例锁文件路径（本机唯一、非共享盘路径；也用于推导互斥名）。</param>
    /// <param name="pipeName">激活管道名（每个实例唯一，写入锁文件供第二个实例读取）。</param>
    /// <param name="onMessage">收到管道消息时的回调（在管道线程上调用，需自行编组）。</param>
    public SingleInstanceGuard(string lockFilePath, string pipeName, Func<string, Task> onMessage)
    {
        _lockFilePath = lockFilePath;
        _pipeName = pipeName;
        _onMessage = onMessage;
    }

    /// <summary>
    /// 尝试成为唯一实例。
    /// </summary>
    /// <returns>
    /// true = 本进程是第一个实例（已持有锁、已开始监听管道）；
    /// false = 已有实例在运行（本进程应退出）。
    /// </returns>
    public bool TryAcquire()
    {
        var mutexName = MutexName(_lockFilePath);

        // ① 单例锁：命名互斥（跨平台可靠）。
        //    PCL-CE 原版用「独占打开文件 + FileShare.Read」实现，该组合只在 Windows 上互斥：
        //    Unix 上 .NET 的文件共享是咨询锁，FileShare.Read 不阻止他人以
        //    FileMode.Create + FileAccess.ReadWrite 再打开同一文件 → 单实例失效。
        //    这个坑由单元测试抓到（见 SingleInstanceGuardTests），故改用 Mutex。
        var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        try
        {
            // 把本进程的管道名写进锁文件：第二个实例需要知道该连哪里。
            // 文件内容每次覆盖写（首个实例退出时删除，不会读到过期值）。
            var dir = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_lockFilePath, _pipeName);
        }
        catch (Exception)
        {
            // 锁文件写失败不应拦住启动（互斥已经拿到，单实例本身仍有效）
            mutex.Dispose();
            throw;
        }

        _mutex = mutex;
        _ownsLock = true;

        // ② 激活通道：命名管道服务端
        try
        {
            _pipe = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            _ = ListenAsync(_cts.Token);
        }
        catch (Exception)
        {
            // 管道起不来不影响单实例本身，只影响"第二个实例能否激活首个窗口"
            _pipe = null;
        }

        return true;
    }

    /// <summary>
    /// 通知「已在运行的实例」激活主窗口（由第二个实例调用）。
    /// 第二个实例从锁文件里读出管道名后连上去。
    /// </summary>
    /// <param name="lockFilePath">与首个实例一致的锁文件路径。</param>
    /// <param name="message">要发送的消息，如 <c>activate</c>。</param>
    public static async Task<bool> TryNotifyExistingAsync(string lockFilePath, string message)
    {
        string pipeName;
        try
        {
            pipeName = File.ReadAllText(lockFilePath).Trim();
        }
        catch (Exception)
        {
            // 锁文件读不到（例：首个实例刚退出）→ 视为无实例
            return false;
        }

        if (string.IsNullOrEmpty(pipeName)) return false;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(1000).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, Encoding.UTF8, 8, true);
            await writer.WriteLineAsync(message).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // 管道连不上 → 首个实例可能正在退出，调用方可自行决定是否退出
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _pipe is not null)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(_pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    await _onMessage(line.Trim()).ConfigureAwait(false);
                }

                _pipe.Disconnect();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 单次连接失败不应让监听线程死亡：重建管道继续等下一个实例
                try { _pipe.Disconnect(); }
                catch (Exception)
                {
                    // 管道可能已断开，忽略
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pipe is not null)
        {
            try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            _pipe = null;
        }

        if (_mutex is not null && _ownsLock)
        {
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
            _ownsLock = false;
        }

        if (_mutex is not null)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        // ③ 退出即删锁文件：让第二个实例读不到过期管道名
        try
        {
            if (File.Exists(_lockFilePath)) File.Delete(_lockFilePath);
        }
        catch (Exception)
        {
            // 删不掉不影响退出
        }
    }
}
