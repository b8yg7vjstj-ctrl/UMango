using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UMango.Core.App.Tasks;
using Xunit;

namespace UMango.Core.Tests.App;

/// <summary>
/// 任务栏进度聚合的测试。
///
/// ⚠ <see cref="TaskCenter"/> 是**全局静态状态**，因此本类必须与其它触碰它的
/// 测试串行（与仓库既有约定一致：见 InstalledListTests 的 [Collection("静态状态")]）。
/// 每个用例结束都会清空 Tasks，避免污染后续用例。
/// </summary>
[Collection("静态状态")]
public class TaskProgressAggregatorTests
{
    public TaskProgressAggregatorTests() => TaskCenter.Tasks.Clear();

    private static TaskModel NewRunning(double progress, bool supportProgress = true)
    {
        var t = supportProgress ? TaskModel.Progressive("任务") : TaskModel.Simple("任务");
        t.State = TaskState.Running;
        t.Progress = progress;
        return t;
    }

    [Fact]
    public void 没有任务时不显示进度()
    {
        var s = TaskProgressAggregator.Summarize(Array.Empty<TaskModel>());
        Assert.False(s.HasActive);
        Assert.Null(s.Fraction);
        Assert.Equal(0, s.TotalCount);
    }

    [Fact]
    public void 全部完成时不显示进度()
    {
        var done = TaskModel.Progressive("已完成");
        done.State = TaskState.Success;
        done.Progress = 1.0;

        var s = TaskProgressAggregator.Summarize(new[] { done });
        Assert.False(s.HasActive);
        Assert.Equal(1, s.TotalCount);
        Assert.Equal(0, s.ActiveCount);
    }

    [Fact]
    public void 单个进行中任务取其进度()
    {
        var s = TaskProgressAggregator.Summarize(new[] { NewRunning(0.42) });
        Assert.True(s.HasActive);
        Assert.Equal(0.42, s.Fraction!.Value, precision: 3);
    }

    [Fact]
    public void 多个进行中任务取平均进度()
    {
        var s = TaskProgressAggregator.Summarize(new[] { NewRunning(0.2), NewRunning(0.4), NewRunning(0.9) });
        Assert.True(s.HasActive);
        Assert.Equal(0.5, s.Fraction!.Value, precision: 3);
        Assert.Equal(3, s.ActiveCount);
    }

    [Fact]
    public void 等待中任务计入活跃且按0进度参与平均()
    {
        var waiting = TaskModel.Progressive("排队");
        // State 默认就是 Waiting
        var s = TaskProgressAggregator.Summarize(new[] { waiting, NewRunning(0.6) });
        Assert.True(s.HasActive);
        Assert.Equal(2, s.ActiveCount);
        Assert.Equal(0.3, s.Fraction!.Value, precision: 3);
    }

    [Fact]
    public void 任一任务不报告进度时为不确定态()
    {
        var s = TaskProgressAggregator.Summarize(new[] { NewRunning(0.5), NewRunning(0.5, supportProgress: false) });
        Assert.True(s.HasActive, "有任务在跑，应显示进度");
        Assert.Null(s.Fraction);  // 但进度不确定
    }

    [Fact]
    public void 进度越界被夹到0到1()
    {
        var s = TaskProgressAggregator.Summarize(new[] { NewRunning(1.7), NewRunning(-0.5) });
        Assert.Equal(0.5, s.Fraction!.Value, precision: 3);
    }

    [Fact]
    public void 失败任务不计入活跃()
    {
        var failed = TaskModel.Progressive("失败");
        failed.State = TaskState.Failed;
        failed.Progress = 0.3;

        var s = TaskProgressAggregator.Summarize(new[] { failed, NewRunning(0.8) });
        Assert.Equal(1, s.ActiveCount);
        Assert.Equal(0.8, s.Fraction!.Value, precision: 3);
        Assert.Equal(2, s.TotalCount);
    }

    [Fact]
    public async Task 进度变化时派发新汇总()
    {
        var task = NewRunning(0.1);
        TaskCenter.Tasks.Add(task);

        var seen = new List<TaskProgressSummary>();
        await using var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));

        // Watch 构造时应立即派发一次初始汇总
        var initial = Assert.Single(seen);
        Assert.True(initial.HasActive);

        task.Progress = 0.9;
        Assert.Equal(2, seen.Count);
        Assert.Equal(0.9, seen[1].Fraction!.Value, precision: 3);

        task.State = TaskState.Success;
        Assert.Equal(3, seen.Count);
        Assert.False(seen[2].HasActive);
    }

    [Fact]
    public async Task 汇总未变化时不重复派发()
    {
        var task = NewRunning(0.5);
        TaskCenter.Tasks.Add(task);

        var seen = new List<TaskProgressSummary>();
        await using var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));
        Assert.Single(seen);

        // 改一个不影响聚合的属性 → 不应派发
        task.StateMessage = "下载中…";
        Assert.Single(seen);
    }

    [Fact]
    public async Task 新增任务会触发重算与派发()
    {
        var seen = new List<TaskProgressSummary>();
        await using var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));
        Assert.Single(seen);
        Assert.False(seen[0].HasActive);

        TaskCenter.Tasks.Add(NewRunning(0.25));
        Assert.Equal(2, seen.Count);
        Assert.True(seen[1].HasActive);
        Assert.Equal(0.25, seen[1].Fraction!.Value, precision: 3);
    }

    [Fact]
    public async Task 移除任务后不再派发它的变化()
    {
        var task = NewRunning(0.1);
        TaskCenter.Tasks.Add(task);

        var seen = new List<TaskProgressSummary>();
        await using var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));
        var count = seen.Count;

        TaskCenter.Tasks.Remove(task);
        Assert.True(seen.Count > count, "移除任务本身应触发一次派发");

        var afterRemove = seen.Count;
        task.Progress = 0.99;   // 已退订，不应再派发
        Assert.Equal(afterRemove, seen.Count);
    }

    [Fact]
    public async Task 订阅建立前已存在的任务其后续变化也要派发()
    {
        // 关键回归：任务先于 Watch 存在。若 watcher 在派发初始值时还没订阅它，
        // 这个任务之后的进度变化就永远收不到（曾真实发生过，故立此用例）。
        var task = NewRunning(0.1);
        TaskCenter.Tasks.Add(task);

        var seen = new List<TaskProgressSummary>();
        await using var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));
        Assert.Single(seen);   // 初始值

        task.Progress = 0.75;
        Assert.Equal(2, seen.Count);
        Assert.Equal(0.75, seen[1].Fraction!.Value, precision: 3);

        task.State = TaskState.Success;
        Assert.Equal(3, seen.Count);
        Assert.False(seen[2].HasActive);
    }

    [Fact]
    public async Task Dispose后不再派发()
    {
        var task = NewRunning(0.1);
        TaskCenter.Tasks.Add(task);

        var seen = new List<TaskProgressSummary>();
        var watcher = TaskProgressAggregator.Watch(s => seen.Add(s));
        var count = seen.Count;
        await watcher.DisposeAsync();

        task.Progress = 0.7;
        TaskCenter.Tasks.Add(NewRunning(0.3));
        Assert.Equal(count, seen.Count);
    }
}
