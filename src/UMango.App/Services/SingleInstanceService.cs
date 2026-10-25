using System;
using System.IO;
using System.Threading.Tasks;
using UMango.Core.App.SingleInstance;
using UMango.Core.Platform;
using UMango.Core.Logging;

namespace UMango.App.Services;

/// <summary>
/// 单实例的 App 侧接线：判定本进程是否为唯一实例，不是则通知已有实例后退出。
///
/// 思路来源：PCL-CE <c>SingleInstanceService</c>（Apache-2.0）的三个机制 ——
/// 单例锁、命名管道传激活指令、由首个实例激活主窗口。
/// 这里的锁/管道路径、激活动作（还原 + 置顶闪烁）按 UMango 自身结构实现。
/// </summary>
public static class SingleInstanceService
{

    private static SingleInstanceGuard? _guard;

    /// <summary>单例锁文件路径（本机用户目录下，每个用户一份）。</summary>
    private static string LockFilePath
    {
        get
        {
            var path = AppPaths.Current.SingleInstanceLockFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            return path;
        }
    }

    /// <summary>本进程的激活管道名（按 PID，保证唯一）。</summary>
    private static string PipeName => $"umango-activate-{Environment.ProcessId}";

    /// <summary>
    /// 启动时调用：true = 继续启动；false = 已有实例，本进程应立即退出。
    /// </summary>
    /// <param name="activateExisting">管道收到 activate 时的回调：把主窗口带到前台。</param>
    public static async Task<bool> TryStartAsync(Func<Task> activateExisting)
    {
        var lockPath = LockFilePath;

        _guard = new SingleInstanceGuard(lockPath, PipeName, async _ =>
        {
            // 管道消息在管道线程上到达，调用方负责编组回 UI 线程
            await activateExisting().ConfigureAwait(false);
        });

        if (_guard.TryAcquire()) return true;

        // 已有实例：通知它激活主窗口，然后让本进程退出
        await SingleInstanceGuard.TryNotifyExistingAsync(lockPath, "activate").ConfigureAwait(false);
        Log.Info("SingleInstance", "检测到已在运行的实例，已通知其激活主窗口，本实例退出");
        return false;
    }

    /// <summary>退出时释放单例锁并删除锁文件。</summary>
    public static async ValueTask DisposeAsync()
    {
        if (_guard is not null)
        {
            await _guard.DisposeAsync().ConfigureAwait(false);
            _guard = null;
        }
    }
}
