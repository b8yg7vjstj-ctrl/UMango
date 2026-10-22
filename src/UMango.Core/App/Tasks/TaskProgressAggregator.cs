using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace UMango.Core.App.Tasks;

/// <summary>
/// 任务栏/全局进度的聚合结果。
/// </summary>
/// <param name="HasActive">是否有进行中的任务（决定是否显示进度）。</param>
/// <param name="Fraction">
/// 聚合进度 0..1；为 null 表示**不确定态**（有任务在跑但都不报告进度，
/// 此时界面应显示不确定态进度条而不是 0%）。
/// </param>
/// <param name="ActiveCount">进行中的任务数。</param>
/// <param name="TotalCount">任务总数（含已完成/失败的）。</param>
public readonly record struct TaskProgressSummary(
    bool HasActive, double? Fraction, int ActiveCount, int TotalCount);

/// <summary>
/// 任务栏/全局进度条应显示的状态（平台中立，不依赖任何 UI 框架的枚举）。
/// App 层负责把它翻译成目标平台的表示（WPF → <c>TaskbarItemProgressState</c>）。
/// </summary>
public enum TaskbarProgressState
{
    /// <summary>不显示进度条。</summary>
    None,

    /// <summary>不确定态（走动条）：有任务在跑但无法给出百分比。</summary>
    Indeterminate,

    /// <summary>确定态：按 <see cref="TaskProgressSummary.Fraction"/> 显示。</summary>
    Normal,
}

/// <summary>
/// 把 <see cref="TaskCenter.Tasks"/> 聚合成一个可显示的总体进度。
///
/// **为什么需要**：PCL-CE 有任务栏进度聚合（下载时 Windows 任务栏图标上
/// 显示进度条），这是"任务在后台跑、用户切到别的窗口"时唯一的进度反馈渠道。
/// UMango 的下载/安装都是长任务，同样需要。
///
/// 聚合口径（与 PCL 一致：按任务条数平均，不按字节数）：
///   · 只统计「等待中 / 进行中」两种状态为活跃；
///   · 全部活跃任务都报告进度 → 平均值；
///   · 只要有任一活跃任务不报告进度 → 整体不确定态（返回 null）；
///   · 没有活跃任务 → <see cref="TaskProgressSummary.HasActive"/> = false。
/// </summary>
public static class TaskProgressAggregator
{
    /// <summary>把一组任务聚合成总体进度。</summary>
    public static TaskProgressSummary Summarize(IEnumerable<TaskModel> tasks)
    {
        var active = new List<TaskModel>();
        var total = 0;

        foreach (var task in tasks)
        {
            total++;
            if (task.State is TaskState.Waiting or TaskState.Running)
                active.Add(task);
        }

        if (active.Count == 0)
            return new TaskProgressSummary(false, null, 0, total);

        // 任一活跃任务不报告进度 → 整体不确定态
        if (active.Any(t => !t.SupportProgress))
            return new TaskProgressSummary(true, null, active.Count, total);

        var sum = active.Sum(t => Clamp01(t.Progress));
        return new TaskProgressSummary(true, sum / active.Count, active.Count, total);
    }

    /// <summary>
    /// 汇总 → 进度条状态的**纯映射**（不碰任何 UI 对象）。
    ///
    /// 之所以放在 Core：这是"有任务在跑该显示什么"的业务判定，与平台无关；
    /// App 层只需把它翻成 WPF 的 TaskbarItemProgressState。放在 Core 还能让
    /// 它在 macOS 开发机上被单元测试（WPF 类型在非 Windows 上加载即崩）。
    /// </summary>
    public static (TaskbarProgressState State, double Value) MapToProgress(TaskProgressSummary summary)
    {
        if (!summary.HasActive)
            return (TaskbarProgressState.None, 0.0);

        if (summary.Fraction is null)
            return (TaskbarProgressState.Indeterminate, 0.0);

        return (TaskbarProgressState.Normal, Clamp01(summary.Fraction.Value));
    }

    /// <summary>
    /// 订阅任务集合与每个任务的属性变化，在聚合结果变化时回调。
    /// 返回的订阅需被 Dispose（用 <c>await using</c>）。
    /// </summary>
    public static TaskProgressWatcher Watch(Action<TaskProgressSummary> onChange)
        => new(onChange);

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// 把「订阅 + 派发」封装起来：既要在任务集合增删时重算，
    /// 也要在单个任务的 <see cref="TaskModel.Progress"/>/<see cref="TaskModel.State"/>
    /// 变化时重算（后者不会触发 <see cref="TaskCenter.Changed"/>）。
    /// </summary>
    public sealed class TaskProgressWatcher : IAsyncDisposable
    {
        private readonly Action<TaskProgressSummary> _onChange;
        private readonly Dictionary<TaskModel, TaskModelSubscription> _subs = [];
        private TaskProgressSummary _last;

        internal TaskProgressWatcher(Action<TaskProgressSummary> onChange)
        {
            _onChange = onChange;
            TaskCenter.Changed += OnChanged;
            // 也要订阅集合本身：TaskCenter.Changed 只在 Register/Complete/Remove 时触发，
            // 而界面完全可能直接往 Tasks（ObservableCollection）里 Add/Remove ——
            // 这条路径不经过上面那个事件，不订阅集合就会漏掉。
            TaskCenter.Tasks.CollectionChanged += OnCollectionChanged;
            Recompute();
        }

        private void OnChanged() => Recompute();

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Recompute();

        /// <summary>
        /// 重算并（若变化）派发。
        /// 构造时会调用一次以派发**初始值**：消费方（如任务栏桥接）需要在
        /// 订阅建立的瞬间就知道当前状态，否则空态下它永远不会被通知。
        /// </summary>
        private bool _initialized;

        private void Recompute()
        {
            // 同步订阅集合：为新任务订阅属性变化，给已消失的任务退订。
            // ⚠ 必须放在初始派发之前：否则构造时已存在的任务不会被订阅，
            //   它们后续的进度变化就永远收不到（这个顺序错过一次，由测试抓出）。
            var seen = new List<TaskModel>();
            foreach (var task in TaskCenter.Tasks)
            {
                seen.Add(task);
                if (!_subs.ContainsKey(task))
                    _subs[task] = new TaskModelSubscription(task, Recompute);
            }

            foreach (var gone in _subs.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _subs[gone].Dispose();
                _subs.Remove(gone);
            }

            var summary = Summarize(TaskCenter.Tasks);

            // 构造时无条件派发一次初始值（空态也要让消费方知道）；
            // 之后仅当汇总变化时才派发，避免无谓刷新。
            if (!_initialized)
            {
                _initialized = true;
                _last = summary;
                _onChange(summary);
                return;
            }

            if (summary != _last)
            {
                _last = summary;
                _onChange(summary);
            }
        }

        public async ValueTask DisposeAsync()
        {
            TaskCenter.Changed -= OnChanged;
            TaskCenter.Tasks.CollectionChanged -= OnCollectionChanged;
            foreach (var sub in _subs.Values) sub.Dispose();
            _subs.Clear();
            await ValueTask.CompletedTask;
        }

        /// <summary>单个任务的属性变化订阅（PropertyChanged）。</summary>
        private sealed class TaskModelSubscription : IDisposable
        {
            private readonly TaskModel _task;
            private readonly Action _onChange;

            internal TaskModelSubscription(TaskModel task, Action onChange)
            {
                _task = task;
                _onChange = onChange;
                _task.PropertyChanged += OnPropertyChanged;
            }

            private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                // Progress/State 影响聚合；Title 等不影响，过滤掉以减少无谓重算
                if (e.PropertyName is nameof(TaskModel.Progress) or nameof(TaskModel.State))
                    _onChange();
            }

            public void Dispose() => _task.PropertyChanged -= OnPropertyChanged;
        }
    }
}
