// SPDX-License-Identifier: Apache-2.0
//
// 设计来源：PCL-CE `Plain Craft Launcher 2/Pages/PageTools/PageToolsTest.xaml.cs`
// （受限许可目录 —— 只借鉴设计语义，独立重写，不复制代码）。
//
// PCL 的「垃圾清理」（`RubbishClear`）把清理逻辑整段写在 UI 事件里：
//   · 在 UI 线程上禁用按钮
//   · 检查"没有正在运行的 Minecraft"且"启动器不在加载中"（PCL 专属状态）
//   · 遍历若干目录，删除缓存 / 日志 / 崩溃报告 / Natives
//   · 数一数删了多少文件，给一个提示
//
// 其中「有运行中的游戏就不能删」这一条是 PCL 专属（MC 进程与游戏目录绑定）。
// UMango 是包管理器启动器，没有 MC 进程，但有**自己的**并发约束：
//   · 正在进行的下载任务（下载临时文件正在写）
//   · 正在进行的安装任务（正在往目标目录写）
//
// 所以本文件把清理拆成**纯计算的规划层**（可测）+ **执行层**（App 侧），
// 而不是把遍历和删除直接写在事件处理器里。

using System;
using System.Collections.Generic;
using System.IO;

namespace UMango.Core.Tools;

/// <summary>清理目标类别。</summary>
public enum CleanupTarget
{
    /// <summary>下载临时目录（未完成的下载残留）。</summary>
    TempDownloads,

    /// <summary>缓存目录。</summary>
    Cache,

    /// <summary>日志目录。</summary>
    Logs,

    /// <summary>更新暂存目录（升级包解压残留）。</summary>
    UpdateStaging,

    /// <summary>崩溃报告目录（如果有）。</summary>
    CrashReports
}

/// <summary>单个清理项的统计。</summary>
/// <param name="Target">目标类别。</param>
/// <param name="Directory">目录绝对路径（不存在时为空串）。</param>
/// <param name="FileCount">文件数量。</param>
/// <param name="TotalBytes">总字节数。</param>
public sealed record CleanupItem(CleanupTarget Target, string Directory, int FileCount, long TotalBytes)
{
    /// <summary>该项目是否有东西可清。</summary>
    public bool HasContent => FileCount > 0;
}

/// <summary>清理计划（一组待清目录的统计）。</summary>
public sealed record CleanupPlan(IReadOnlyList<CleanupItem> Items)
{
    /// <summary>全部目标的文件总数。</summary>
    public int TotalFileCount => Items.Sum(i => i.FileCount);

    /// <summary>全部目标的总字节数。</summary>
    public long TotalBytes => Items.Sum(i => i.TotalBytes);

    /// <summary>没有任何东西可清。</summary>
    public bool IsEmpty => TotalFileCount == 0;

    /// <summary>空计划。</summary>
    public static CleanupPlan Empty { get; } = new([]);
}

/// <summary>清理规划器：算出「哪些目录有多少东西」，不执行任何删除。</summary>
public static class CleanupPlanner
{
    /// <summary>
    /// 统计给定的清理目标。
    /// </summary>
    /// <param name="directories">目标 → 目录路径的映射。路径为空或目录不存在时跳过。</param>
    /// <param name="recursive">是否递归统计子目录（默认 true）。</param>
    public static CleanupPlan Plan(IReadOnlyDictionary<CleanupTarget, string> directories, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var items = new List<CleanupItem>();
        foreach (var (target, directory) in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                items.Add(new CleanupItem(target, directory ?? "", 0, 0));
                continue;
            }

            long bytes = 0;
            var count = 0;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(directory, "*", option))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (IOException)
                {
                    // 文件可能刚好被删掉（并发清理）——跳过，不让整个统计崩掉。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权读取 —— 跳过这一项，不让整个统计崩掉。
                }
            }

            items.Add(new CleanupItem(target, directory, count, bytes));
        }

        return new CleanupPlan(items);
    }

    /// <summary>
    /// 删除计划里列出的内容。
    ///
    /// 约定：只删**目录里的内容**，保留目录本身（删掉目录会让上层代码
    /// 下次写入时少一次「建目录」的检查，但也会让用户"设置里指的路径"消失）。
    /// </summary>
    /// <param name="plan">待执行的计划。</param>
    /// <returns>实际删除的文件数与字节数。</returns>
    public static (int Files, long Bytes) Execute(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var files = 0;
        long bytes = 0;

        foreach (var item in plan.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Directory) || !Directory.Exists(item.Directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(item.Directory, "*", SearchOption.AllDirectories))
            {
                long length = 0;
                try
                {
                    length = new FileInfo(file).Length;
                    File.Delete(file);
                    files++;
                    bytes += length;
                }
                catch (IOException)
                {
                    // 正在被占用 —— 跳过。
                }
                catch (UnauthorizedAccessException)
                {
                    // 无权删除 —— 跳过。
                }
            }
        }

        return (files, bytes);
    }
}
