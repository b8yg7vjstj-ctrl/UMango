// SPDX-License-Identifier: Apache-2.0
//
// 加载器的任务栏进度聚合。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs` 中的
// `ModLoader.LoaderTaskbarProgressGet` / `LoaderTaskbarProgressRefresh`
// （自定义许可目录 → **只借鉴设计语义，独立重写**）。
//
// PCL 的口径：
//   · 没有任务 → 进度 1（= 不显示）；
//   · 有任务 → **按条数取平均**（不是按字节数），并夹到 0..1；
//   · 任务栏状态：0 或 1 → None；< 0.015 → Indeterminate；否则 Normal；
//   · 平滑：进度下降时立刻用新值；上升时用 `0.9*旧 + 0.1*新`（避免抖动）。
//
// UMango 的差异：
//   ① PCL 的平滑是**全局静态**的，且只在"刷新"时被动更新；UMango 做成
//      **纯函数** `Smooth(previous, current)`，便于测试。
//   ② PCL 的"完成/中止就移出任务列表"逻辑耦合在 UI 刷新里；UMango 提供
//      `FilterActive` 纯函数。

using System;
using System.Collections.Generic;
using System.Linq;

namespace UMango.Core.App.Loaders;

/// <summary>加载器任务栏应显示的状态。</summary>
public enum LoaderTaskbarState
{
    /// <summary>不显示进度。</summary>
    None,

    /// <summary>不确定态（走动条）。</summary>
    Indeterminate,

    /// <summary>确定态，按 <see cref="LoaderTaskbarProgress.Fraction"/> 显示。</summary>
    Normal,
}

/// <summary>任务栏进度的聚合结果。</summary>
public readonly record struct LoaderTaskbarProgress(
    LoaderTaskbarState State, double? Fraction, int ActiveCount, int TotalCount);

/// <summary>加载器进度聚合器（纯函数，无全局状态）。</summary>
public static class LoaderProgressAggregator
{
    /// <summary>低于此进度视为不确定态（与 PCL 一致）。</summary>
    public const double IndeterminateThreshold = 0.015d;

    /// <summary>
    /// 聚合一组加载器的进度。
    ///
    /// 口径与 PCL 一致：**按条数平均**，不是按权重。
    /// </summary>
    public static LoaderTaskbarProgress Summarize(IEnumerable<LoaderBase> loaders)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        var list = loaders.Where(l => l is not null).ToList();
        if (list.Count == 0)
            return new LoaderTaskbarProgress(LoaderTaskbarState.None, null, 0, 0);

        var active = list.Where(l => l.State == LoaderState.Running).ToList();
        if (active.Count == 0)
            return new LoaderTaskbarProgress(LoaderTaskbarState.None, null, 0, list.Count);

        // 有任务在跑但都不报告进度 → 不确定态
        if (active.All(l => !l.HasProgress))
            return new LoaderTaskbarProgress(LoaderTaskbarState.Indeterminate, null, active.Count, list.Count);

        var fraction = Math.Clamp(active.Average(l => l.Progress), 0d, 1d);
        var state = fraction <= 0d || fraction >= 1d
            ? LoaderTaskbarState.None
            : fraction < IndeterminateThreshold
                ? LoaderTaskbarState.Indeterminate
                : LoaderTaskbarState.Normal;

        return new LoaderTaskbarProgress(state, fraction, active.Count, list.Count);
    }

    /// <summary>
    /// 平滑进度（与 PCL 的 `loaderTaskbarProgress` 同语义）。
    ///
    /// PCL 的规则：
    ///   · 新进度 ≤ 0 或 ≥ 1 → 直接用新值；
    ///   · 新进度比旧值小（倒退）→ 直接用新值；
    ///   · 否则 → `0.9*旧 + 0.1*新`（缓慢上升，避免抖动）。
    /// </summary>
    public static double Smooth(double previous, double current)
    {
        if (current <= 0d || current >= 1d)
            return current;
        if (previous > current)
            return current;
        return previous * 0.9d + current * 0.1d;
    }

    /// <summary>
    /// 挑出仍需显示的加载器（PCL 的"完成/中止/等待就移出任务列表"逻辑）。
    /// </summary>
    public static List<LoaderBase> FilterActive(IEnumerable<LoaderBase> loaders)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        return loaders.Where(l => l is not null && (l.State == LoaderState.Running || l.State == LoaderState.Waiting)).ToList();
    }
}
