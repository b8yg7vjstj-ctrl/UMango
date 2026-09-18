// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（PCL.Core/App/Tasks/ITaskCancelable.cs，Apache-2.0）。
namespace UMango.Core.App.Tasks;

/// <summary>可取消的任务</summary>
public interface ITaskCancelable
{
    /// <summary>取消任务</summary>
    void Cancel();
}
