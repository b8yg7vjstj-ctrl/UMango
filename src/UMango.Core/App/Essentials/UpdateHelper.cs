using System;
using System.IO;
using System.Threading;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 自更新的「文件替换」原语。
///
/// 对应 PCL-CE 的 <c>PCL.Core/App/Essentials/UpdateHelper.cs</c>（Apache-2.0）。
/// 原版逻辑逐行移植，仅把 <c>Basics.GetParentPathOrEmpty</c> 换成 <c>Path.GetDirectoryName</c>。
///
/// **为什么需要 FileSystemWatcher 等删除事件**：
/// Windows 上 <c>File.Delete</c> 对「正在被杀软扫描/索引」的文件可能返回成功但删除延后，
/// 紧接着 <c>File.Copy</c> 就会因目标仍存在而失败。等 Deleted 事件能确保真的删掉了。
///
/// **为什么先备份再删**：复制失败时可以把备份移回去，至少不让程序变成「缺文件」状态。
/// </summary>
public static class UpdateHelper
{
    /// <summary>
    /// 等待「文件已删除」事件的最长时间。公开是为了让测试能验证等待语义
    /// （某些文件系统不派发 FileSystemWatcher 事件，超时是唯一的兜底）。
    /// </summary>
    public static readonly TimeSpan DeleteWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 用 <paramref name="source"/> 替换 <paramref name="target"/>（原子性尽力而为）。
    /// </summary>
    /// <param name="source">用于替换的来源文件路径</param>
    /// <param name="target">目标文件路径</param>
    /// <returns>成功返回 null；失败返回异常（此时若可能已恢复原文件）</returns>
    public static Exception? Replace(string source, string target)
    {
        var backup = $"{target}.bak.{DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
        Exception? lastEx = null;
        try
        {
            source = Path.GetFullPath(source);
            target = Path.GetFullPath(target);

            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory))
                throw new DirectoryNotFoundException($"无法确定目标文件所在目录：{target}");

            // ① 备份目标文件
            File.Copy(target, backup);
            if (!File.Exists(backup)) throw new FileNotFoundException("备份目标文件失败", backup);

            // ② 删除原文件并等待文件删除事件
            var deletedEvent = new ManualResetEventSlim(false);
            using var watcher = new FileSystemWatcher(directory, Path.GetFileName(target));
            watcher.Deleted += (_, _) => deletedEvent.Set();
            watcher.EnableRaisingEvents = true;
            File.Delete(target);
            if (!deletedEvent.Wait(DeleteWaitTimeout)) throw new TimeoutException("删除目标文件失败");
            watcher.EnableRaisingEvents = false;

            // ③ 复制到目标文件
            File.Copy(source, target);
            if (!File.Exists(target)) throw new FileNotFoundException("复制到目标文件失败", target);
        }
        catch (Exception ex)
        {
            // 出错：恢复原文件并返回异常
            if (File.Exists(backup) && !File.Exists(target)) File.Move(backup, target);
            lastEx = ex;
        }

        // ④ 清理备份（无论成功失败：成功则备份无用，失败则已恢复或恢复无意义）
        if (File.Exists(backup)) File.Delete(backup);
        return lastEx;
    }
}
