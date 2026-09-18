// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（PCL.Core/App/Tasks/ITaskGroup.cs，Apache-2.0）。
namespace UMango.Core.App.Tasks;

/// <summary>子任务增删事件</summary>
public delegate void TaskGroupEvent(ITask task);

/// <summary>
/// 任务组。**这是「按不同下载方式列明子项进度」的树形载体**：
/// 界面上一行父任务（如「安装 express」），展开后是按下载方式/阶段划分的子项，
/// 每个子项自带进度条。
/// </summary>
public interface ITaskGroup : ITask
{
    /// <summary>新增子任务</summary>
    event TaskGroupEvent AddTask;
    /// <summary>移除子任务</summary>
    event TaskGroupEvent RemoveTask;
}
