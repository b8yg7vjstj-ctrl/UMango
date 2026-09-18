// SPDX-License-Identifier: Apache-2.0
// 采用并改造自 PCL-CE（PCL.Core/App/Tasks/TaskCenter.cs，Apache-2.0）。
// 改造点：以 UI 线程安全的方式对外暴露任务集合；不依赖 WPF 的 Dispatcher。
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace UMango.Core.App.Tasks;

/// <summary>
/// 任务中心：全局任务注册表。界面绑定 <see cref="Tasks"/> 即可获得
/// 下载进度页需要的全部任务与子项。
/// </summary>
public static class TaskCenter
{
    /// <summary>当前所有任务（界面用 ObservableCollection 直接绑定）</summary>
    public static ObservableCollection<TaskModel> Tasks { get; } = [];

    private static readonly ConcurrentDictionary<TaskModel, byte> Running = new();

    /// <summary>任务数量变化时触发（用于「管理记录/次数记录」统计）</summary>
    public static event Action? Changed;

    /// <summary>已累计完成的任务数（次数记录的来源）</summary>
    public static int CompletedCount { get; private set; }

    /// <summary>已累计失败的任务数</summary>
    public static int FailedCount { get; private set; }

    /// <summary>注册一个新任务并返回它，供调用方继续更新进度。</summary>
    public static TaskModel Register(TaskModel task)
    {
        Tasks.Add(task);
        Changed?.Invoke();
        return task;
    }

    /// <summary>把任务标记为完成并更新统计。</summary>
    public static void Complete(TaskModel task, bool success)
    {
        task.State = success ? TaskState.Success : TaskState.Failed;
        task.Progress = success ? 1.0 : task.Progress;
        Running.TryRemove(task, out _);
        if (success) CompletedCount++; else FailedCount++;
        Changed?.Invoke();
    }

    /// <summary>从列表中移除任务（不影响统计）。</summary>
    public static void Remove(TaskModel task)
    {
        Tasks.Remove(task);
        Running.TryRemove(task, out _);
        Changed?.Invoke();
    }
}
