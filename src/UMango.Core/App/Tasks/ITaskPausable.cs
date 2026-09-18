// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（PCL.Core/App/Tasks/ITaskPausable.cs，Apache-2.0）。
namespace UMango.Core.App.Tasks;

/// <summary>可暂停/继续的任务（下载类任务常用）</summary>
public interface ITaskPausable
{
    /// <summary>暂停</summary>
    void Pause();
    /// <summary>继续</summary>
    void Resume();
}
