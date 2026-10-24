// SPDX-License-Identifier: Apache-2.0
//
// 加载器（Loader）状态机。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs`
// （1,032 行）。该文件位于 PCL 的**自定义许可目录**（《PCL 分发有限许可》），
// 因此 UMango **只借鉴设计语义，独立重写，不复制任何代码**。
//
// 与 PCL 原版的对应关系：
//   PCL `ModBase.LoadState`  →  本 `LoaderState`
//   PCL `LoaderBase`         →  本 `LoaderBase`
//   PCL `LoaderTask<,>`      →  本 `LoaderTask<,>`
//   PCL `LoaderCombo`        →  本 `LoaderCombo`
//   PCL `ModLoader.LoaderFolderRun` → 本 `LoaderFolderWatcher`
//
// 关键差异（都是改进，不是抄）：
//   ① PCL 用 `Thread` + `ThreadInterruptedException` + `Thread.Sleep(10)` 轮询等待；
//      UMango 用 `Task` + `CancellationToken` + `await`，无轮询、无线程中断。
//   ② PCL 的 `LoaderTask` 用「比较 `Task.Id`」判断自己是否被新线程取代；
//      UMango 用单调递增的**运行代次（generation）**，语义更清楚且无装箱。
//   ③ PCL 的 `WaitForExit` 会**阻塞调用线程**；UMango 提供 `WaitForExitAsync`，
//      不阻塞 UI 线程。

namespace UMango.Core.App.Loaders;

/// <summary>加载器状态。</summary>
public enum LoaderState
{
    /// <summary>等待中（尚未开始，或已结束后被重置）。</summary>
    Waiting,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>已成功完成。</summary>
    Succeeded,

    /// <summary>失败（见 <see cref="LoaderBase.Error"/>）。</summary>
    Failed,

    /// <summary>被中止（用户取消或父级级联中止）。</summary>
    Aborted,
}
