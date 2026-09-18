// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（PCL.Core/App/Tasks/ITask.cs，Apache-2.0）。
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.Tasks;

/// <summary>
/// 任务状态改变事件。
/// </summary>
/// <param name="state">当前状态</param>
/// <param name="message">状态消息（会显示在界面上）</param>
public delegate void TaskStateEvent(TaskState state, string message);

/// <summary>
/// 可执行任务。包管理器的每一次安装/卸载/下载都是一个 ITask，
/// 由 <see cref="TaskCenter"/> 统一调度，界面订阅其状态与进度事件。
/// </summary>
public interface ITask
{
    /// <summary>任务标题（界面主行显示）</summary>
    string Title { get; }

    /// <summary>运行任务</summary>
    /// <param name="cancelToken">取消令牌</param>
    Task ExecuteAsync(CancellationToken cancelToken = default);

    /// <summary>任务状态改变事件</summary>
    event TaskStateEvent StateChanged;
}
