using System;
using System.Collections.Generic;
using System.Linq;
using UMango.Core.App.Tasks;
using UMango.Core.Logging;
using UMango.Core.UI;

namespace UMango.App.Services;

/// <summary>
/// 把任务完成事件转成 Windows 系统通知（Toast）。
///
/// **为什么需要**：包管理器最常见的场景是"开始安装 → 切到别的窗口等着"。
/// 应用内右下角浮条只在 UMango 前台时可见，任务做完用户无从得知；
/// 系统 Toast 是唯一能在窗口不在前台时告知"做完了/失败了"的渠道。
/// 对应 PCL-CE 的 <c>ToastNotification</c>（PCL.Core，Apache-2.0）。
///
/// **平台边界**：底层是 WinRT COM 互操作（裸 vtable），只在 Windows 上可用。
/// 这里用 <see cref="OperatingSystem.IsWindows"/> 做**运行期**守卫而不是编译期条件 ——
/// App 工程只 target net10.0-windows，编译期恒真；但同一份代码将来要复用到
/// Avalonia 外壳（macOS/Linux），届时这个判断才是真正的分流点。
/// </summary>
public sealed class ToastBridge : IDisposable
{
    /// <summary>
    /// 只对用户真正"等结果"的任务发通知。正在跑的不发（进度条自己会动），
    /// 取消的不发（用户主动取消，不需要系统再告诉他一次）。
    /// </summary>
    private static bool ShouldNotify(TaskModel task) =>
        task.State is TaskState.Success or TaskState.Failed;

    private readonly HashSet<TaskModel> _announced = new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();

    public ToastBridge()
    {
        TaskCenter.Changed += OnChanged;
    }

    /// <summary>
    /// 检查当前任务列表，把"上次还没进终态、这次进了"的任务发成通知。
    ///
    /// 用「上一轮快照对比」而不是直接订阅每个任务的 State 变化：
    /// 任务可能在两次 Changed 之间连续变多次状态（例如失败后又被重试），
    /// 只报终态能让用户收到一条干净的"做完了"而不是一串中间态。
    /// </summary>
    private void OnChanged()
    {
        // 先收集再判断平台：去重集合必须在所有平台上保持准确，
        // 否则同一份代码从 Windows 挪到别的平台时会突然开始重复推送。
        var finished = CollectNewlyFinished();
        if (finished.Count == 0 || !OperatingSystem.IsWindows()) return;

        foreach (var task in finished)
        {
            Send(task);
        }
    }

    /// <summary>
    /// 挑出「本次才进入终态」的任务，并把它们登记为已宣布。
    ///
    /// 单独抽出来是为了可测：平台守卫在 <see cref="OnChanged"/> 里，
    /// 而筛选与去重是平台无关的纯逻辑，不该只能在 Windows 上被验证。
    /// 本仓库未配置 InternalsVisibleTo，故对外公开。
    /// </summary>
    public IReadOnlyList<TaskModel> CollectNewlyFinished()
    {
        lock (_lock)
        {
            return TaskCenter.Tasks.Where(ShouldNotify).Where(t => _announced.Add(t)).ToList();
        }
    }

    private static void Send(TaskModel task)
    {
        try
        {
            if (task.State == TaskState.Failed)
            {
                // 失败要把原因带上：只说"失败"等于没说，用户还得回去看日志
                var reason = string.IsNullOrWhiteSpace(task.StateMessage) ? null : task.StateMessage;
                var body = reason is null ? task.Title : $"{task.Title}：{reason}";
                ToastNotification.SendToast(body, "UMango");
            }
            else
            {
                ToastNotification.SendToast(task.Title, "UMango");
            }
        }
        catch (Exception ex)
        {
            // Toast 是锦上添花：发不出去（没权限/系统关了通知）绝不能影响任务本身
            Log.Debug("Toast", $"发送系统通知失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        TaskCenter.Changed -= OnChanged;
    }
}
