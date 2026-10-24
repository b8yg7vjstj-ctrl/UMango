// SPDX-License-Identifier: Apache-2.0
//
// 文件夹变更驱动的加载器触发。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs` 中的
// `ModLoader.LoaderFolderRun`（自定义许可目录 → **只借鉴设计语义，独立重写**）。
//
// PCL 的做法：维护一张 `Dictionary<LoaderBase, LoaderFolderDictionaryEntry>`，
// 记录每个加载器上次看到的文件夹最后写入时间；再次调用时若时间没变就跳过。
// 最后写入时间取**递归的最大值**（`GetActualLastWriteTimeUtc`）。
//
// UMango 的差异：
//   ① PCL 的字典是**静态全局**且以加载器对象为键（加载器被回收就泄漏条目）；
//      UMango 做成一个**实例**，由调用方持有，生命周期明确。
//   ② PCL 的比较用 `LoaderFolderDictionaryEntry.Equals`（比较时间+路径），
//      但**写入时用的是新算出来的 value**，而判断时比的是**旧条目**；
//      UMango 显式写成两个可读的方法。
//   ③ PCL 的 `GetActualLastWriteTimeUtc` 在 `maxDepth == 0` 时只看目录本身；
//      UMango 保持一致（0 = 不递归）。

using System;
using System.Collections.Generic;
using System.IO;

namespace UMango.Core.App.Loaders;

/// <summary>文件夹触发类型。</summary>
public enum LoaderFolderRunType
{
    /// <summary>文件夹有更新才运行。</summary>
    RunOnUpdated,

    /// <summary>总是运行。</summary>
    ForceRun,

    /// <summary>只检查并更新记录，不运行。</summary>
    UpdateOnly,
}

/// <summary>文件夹变更观察器：记住每个加载器上次看到的文件夹时间戳。</summary>
public sealed class LoaderFolderWatcher
{
    private readonly Dictionary<LoaderBase, FolderEntry> _entries = [];

    /// <summary>当前记录的条目数（用于诊断）。</summary>
    public int TrackedCount => _entries.Count;

    /// <summary>
    /// 检查文件夹是否变化，必要时运行加载器。
    /// </summary>
    /// <param name="loader">要触发的加载器。</param>
    /// <param name="folderPath">被观察的文件夹。</param>
    /// <param name="type">触发类型。</param>
    /// <param name="maxDepth">递归深度（0 = 只看自己）。</param>
    /// <param name="extraPath">额外参与比较的路径（不传给加载器）。</param>
    /// <param name="loaderInput">传给加载器的输入（默认用文件夹路径）。</param>
    /// <returns>是否真的运行了加载器。</returns>
    public bool Run(LoaderBase loader, string folderPath, LoaderFolderRunType type,
        int maxDepth = 0, string extraPath = "", object? loaderInput = null)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var watchPath = folderPath + extraPath;
        var current = new FolderEntry(watchPath, GetLastWriteTimeUtc(watchPath, maxDepth));

        if (type == LoaderFolderRunType.RunOnUpdated && _entries.TryGetValue(loader, out var previous))
        {
            // 只有"路径相同且时间没变"才跳过。
            //
            // 注意「上次不存在」的情况：previous.LastWriteTimeUtc 为 null 时
            // **不能**当作"没变"—— 目录从不存在变成存在，是一次更新，要跑。
            var unchanged = previous.Path == current.Path
                           && previous.LastWriteTimeUtc is not null
                           && previous.LastWriteTimeUtc == current.LastWriteTimeUtc;
            if (unchanged)
                return false;
        }

        // 记录本次检查结果（无论是否运行）
        _entries[loader] = current;

        if (type == LoaderFolderRunType.UpdateOnly)
            return false;

        loader.Start(loaderInput ?? folderPath, type == LoaderFolderRunType.ForceRun);
        return true;
    }

    /// <summary>忘记某个加载器的记录。</summary>
    public void Forget(LoaderBase loader) => _entries.Remove(loader);

    /// <summary>清空全部记录。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// 取目录的"实际最后写入时间"：取自身与所有子目录（受 maxDepth 限制）的最大值。
    ///
    /// 目录本身的写入时间不随**文件内容**变化而更新（只随目录项增删变），
    /// 所以必须递归子目录才能反映"里面的东西变了"。
    /// </summary>
    public static DateTime? GetLastWriteTimeUtc(string path, int maxDepth = 0)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
                return null;

            var time = info.LastWriteTimeUtc;
            if (maxDepth > 0)
            {
                foreach (var sub in info.EnumerateDirectories())
                {
                    var subTime = GetLastWriteTimeUtc(sub.FullName, maxDepth - 1);
                    if (subTime is { } t && t > time)
                        time = t;
                }
            }

            return time;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct FolderEntry(string Path, DateTime? LastWriteTimeUtc);
}
