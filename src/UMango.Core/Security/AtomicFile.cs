namespace UMango.Core.Security;

/// <summary>
/// 原子文件写入。
///
/// **为什么要原子写**：直接往目标文件写时，进程在写一半被杀会留下半截文件。
/// 对于密钥文件，这意味着下次启动读到它会判定「密钥不可用」而重建 ——
/// 用户被迫重新登录。原子写保证目标文件要么是完整的旧内容、要么是完整的新内容。
///
/// 做法：同目录写临时文件 → flush 到磁盘 → 覆盖式移动到目标。
/// 同目录是必须的：跨目录的移动在 Windows 上可能是复制（非原子）。
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// 观察点：临时文件写完、目标文件尚未被替换时被调用。
    ///
    /// 形参是（目标路径, 临时文件路径）。生产代码不设置它。
    ///
    /// **为什么需要它**：`SecretService` 调 `AtomicFile.Write` 时不会传回调，
    /// 所以「SecretService 到底有没有走原子写」在那一层是观察不到的
    /// （直写目标与原子写产出的字节完全一样）。
    /// 这个观察点让上层能确认「确实经过了原子写的替换时机」。
    /// </summary>
    public static Action<string, string>? Observer { get; set; }

    /// <summary>
    /// 原子写入。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="data">要写的内容。</param>
    /// <param name="onTempWritten">
    /// 临时文件**已写完并落盘**、目标文件尚未被替换时的回调。
    ///
    /// 这是给测试用的观察点：借它可以确认「目标文件在替换前仍是旧内容」
    /// 以及「临时文件里确实是新内容」。
    /// 生产代码不用传。
    /// </param>
    public static void Write(string path, byte[] data, Action<string>? onTempWritten = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var tmpFile = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.tmp{Random.Shared.Next(10000, 99999)}");

        try
        {
            // 临时文件与目标同目录：跨目录的移动在 Windows 上可能是复制（非原子）。
            using (var stream = new FileStream(tmpFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                // flush 到磁盘：只 Flush（不 fsync）的话，掉电时数据可能还在页缓存里。
                stream.Flush(flushToDisk: true);
            }

            // 此刻目标文件仍是旧内容 —— 这就是「原子」的可观察形态。
            Observer?.Invoke(fullPath, tmpFile);
            onTempWritten?.Invoke(tmpFile);

            // 覆盖式移动：同卷上的 rename 是原子的。
            File.Move(tmpFile, fullPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { /* 清理失败可忽略 */ }
            throw;
        }
    }
}
