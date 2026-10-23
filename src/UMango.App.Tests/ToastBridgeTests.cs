using UMango.App.Services;
using UMango.Core.App.Tasks;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 系统通知桥接的测试。
///
/// **为什么这些用例在 macOS 上也能跑**：平台守卫只挡"发送"，
/// 筛选与去重抽在 <see cref="ToastBridge.CollectNewlyFinished"/> 里，
/// 与平台无关，可以直接断言它挑出了谁、登记了谁。
/// 真机上是否真弹 Toast，属 Parallels 那台 Windows 11 的手动验收项。
///
/// 不 mock WinRT 本身（那是裸 COM 互操作，macOS 上连程序集都没有）——
/// 本类验证的是"该通知哪些、且每个只通知一次"这一层语义。
/// </summary>
[Collection("静态状态")]
public class ToastBridgeTests
{
    private static TaskModel NewTask(string title, TaskState state)
    {
        var t = TaskModel.Simple(title);
        t.State = state;
        return t;
    }

    /// <summary>
    /// 成功与失败都是用户"等结果"的状态，都该被挑出来。
    /// 若有人把判据写漏（例如只留 Success 忘了 Failed），这个断言会变。
    /// </summary>
    [Fact]
    public void 成功与失败都会被挑出()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        var success = NewTask("装好了", TaskState.Success);
        var failed = NewTask("装失败", TaskState.Failed);
        TaskCenter.Tasks.Add(success);
        TaskCenter.Tasks.Add(failed);

        var picked = bridge.CollectNewlyFinished();

        Assert.Equal(2, picked.Count);
        Assert.Contains(success, picked);
        Assert.Contains(failed, picked);
    }

    /// <summary>
    /// 正在跑/等待中的任务不挑：进度条自己会动，不需要系统再报一次。
    /// </summary>
    [Fact]
    public void 非终态任务不会被挑出()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        TaskCenter.Tasks.Add(NewTask("正在装", TaskState.Running));
        TaskCenter.Tasks.Add(NewTask("排队中", TaskState.Waiting));

        Assert.Empty(bridge.CollectNewlyFinished());
    }

    /// <summary>
    /// 取消的任务不挑：用户主动取消，系统再通知一次是打扰。
    /// </summary>
    [Fact]
    public void 已取消的任务不会被挑出()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        TaskCenter.Tasks.Add(NewTask("不想装了", TaskState.Canceled));

        Assert.Empty(bridge.CollectNewlyFinished());
    }

    /// <summary>
    /// 同一任务第二次不再被挑出 —— 否则一个任务会连弹多条 Toast。
    /// </summary>
    [Fact]
    public void 同一任务不会重复挑出()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        var task = NewTask("只该报一次", TaskState.Success);
        TaskCenter.Tasks.Add(task);

        Assert.Single(bridge.CollectNewlyFinished());
        Assert.Empty(bridge.CollectNewlyFinished());
        Assert.Empty(bridge.CollectNewlyFinished());
    }

    /// <summary>
    /// 两个同标题的任务是**两个不同的任务**，都要通知 ——
    /// 用标题做去重会让"装 A 和装 B"只报一条。
    /// </summary>
    [Fact]
    public void 同标题的不同任务都会通知()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        TaskCenter.Tasks.Add(NewTask("安装依赖", TaskState.Success));
        TaskCenter.Tasks.Add(NewTask("安装依赖", TaskState.Success));

        Assert.Equal(2, bridge.CollectNewlyFinished().Count);
    }

    /// <summary>
    /// 任务从进行中走到完成，第二轮才被挑出 ——
    /// 这是"只在终态报一次"这条语义的主路径。
    /// </summary>
    [Fact]
    public void 任务进入终态后才被挑出()
    {
        TaskCenter.Tasks.Clear();
        using var bridge = new ToastBridge();

        var task = NewTask("下载中", TaskState.Running);
        TaskCenter.Tasks.Add(task);

        Assert.Empty(bridge.CollectNewlyFinished());

        task.State = TaskState.Success;
        Assert.Single(bridge.CollectNewlyFinished());
    }

    /// <summary>
    /// 释放后不再登记 —— 否则窗口关了还在弹通知。
    /// 走真实的 <c>TaskCenter.Changed</c> 触发路径：桥接已退订，
    /// 集合不应增长（反射读私有计数）。
    /// </summary>
    [Fact]
    public void 释放后不再登记()
    {
        TaskCenter.Tasks.Clear();
        var bridge = new ToastBridge();

        TaskCenter.Tasks.Add(NewTask("窗口还开着", TaskState.Success));
        TaskCenter.Complete(TaskCenter.Tasks[0], true);
        Assert.Equal(1, AnnouncedCount(bridge));

        bridge.Dispose();

        TaskCenter.Tasks.Add(NewTask("窗口已关", TaskState.Success));
        TaskCenter.Complete(TaskCenter.Tasks[^1], true);
        Assert.Equal(1, AnnouncedCount(bridge));
    }

    /// <summary>读私有 _announced 集合的计数（测试需要观察这个内部状态）。</summary>
    private static int AnnouncedCount(ToastBridge bridge)
    {
        var field = typeof(ToastBridge).GetField("_announced",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var set = field!.GetValue(bridge);
        Assert.NotNull(set);
        // HashSet<T> 不实现非泛型 ICollection，Count 只能反射取
        var count = (int)set!.GetType().GetProperty("Count")!.GetValue(set)!;
        return count;
    }
}