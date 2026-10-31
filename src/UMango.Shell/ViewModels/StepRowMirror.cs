// SPDX-License-Identifier: Apache-2.0
//
// 子项行镜像：把「Core 的子项 + 终端输出」整理成界面可直接渲染的行集合。
//
// 从 WPF 壳（src/UMango.App/Views/ProgressView.xaml.cs）下沉到 Shell，
// 让 WPF 壳与 Avalonia 壳共用同一份实现 —— 需求里的「降低重复代码率」。
//
// 这一层**不含任何 UI 框架类型**，因此：
//   · 可在 macOS 上单元测试（两个壳的进度页都靠它保证行为一致）；
//   · WPF 壳的 DataTrigger 与 Avalonia 壳的转换器读的是**同一批字段**。

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using UMango.Core.App.Tasks;
using UMango.Core.Terminal;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 子项行镜像。
///
/// 职责：
///   1. 让「行」与当前选中任务的 <see cref="TaskModel.Children"/> 对齐
///      （补新建、摘旧的、顺序对齐）；
///   2. 把终端输出按 <see cref="TerminalLine.Step"/> 分发到它所属的那一行；
///   3. 按调试模式裁剪每行显示的行数（关闭时只留最近若干行 + 省略提示）。
///
/// 行对象一旦建好就**永不替换** —— 界面模板持有的是行实例，换掉会让那一路输出断流。
/// </summary>
public sealed class StepRowMirror
{
    /// <summary>非调试模式下每个子项最多显示的行数。</summary>
    public const int DefaultStepTailLimit = 8;

    private readonly Func<bool> _debugMode;

    /// <summary>当前挂过 Children.CollectionChanged 的那个任务（切任务时要摘掉，避免串台）。</summary>
    private TaskModel? _childHost;

    /// <summary>
    /// 构造镜像。
    /// </summary>
    /// <param name="debugMode">
    /// 读取当前调试模式（Core 的静态开关）。传函数而不是 bool 快照：
    /// 调试模式可以在设置页改，快照会过期。
    /// </param>
    public StepRowMirror(Func<bool> debugMode)
    {
        _debugMode = debugMode ?? throw new ArgumentNullException(nameof(debugMode));
    }

    /// <summary>每个子项一行（含它自己的输出）。</summary>
    public ObservableCollection<StepRow> Rows { get; } = [];

    /// <summary>每个子项最多显示的行数（默认 8；调试模式不裁剪）。</summary>
    public int StepTailLimit { get; set; } = DefaultStepTailLimit;

    /// <summary>
    /// 让行与选中任务的子项对齐：补上新出现的子项、摘掉不再属于它的，
    /// 并把顺序调整成与 Children 完全一致（子项是异步加进来的，顺序必须和进度行对得上）。
    /// </summary>
    public void Rebuild(TaskModel? task)
    {
        AttachChildren(task);

        if (task is not null)
        {
            foreach (var child in task.Children)
                if (RowFor(child) is null)
                    Rows.Add(new StepRow(child));

            // 摘掉不再属于本任务的子项（例如切换了选中任务）。
            for (var i = Rows.Count - 1; i >= 0; i--)
                if (!ContainsChild(task, Rows[i].Child))
                    Rows.RemoveAt(i);

            // 顺序对齐：Move 保持行对象不变，只调整集合中的位置。
            for (var i = 0; i < task.Children.Count; i++)
            {
                var row = RowFor(task.Children[i]);
                if (row is null) continue;
                var current = Rows.IndexOf(row);
                if (current != i) Rows.Move(current, i);
            }
        }
        else
        {
            Rows.Clear();
        }
    }

    /// <summary>整体重建每行的输出（清空后按当前源集合重放）。</summary>
    public void RebuildOutputs(IEnumerable<TerminalLine> source)
    {
        foreach (var row in Rows) row.Reset();
        foreach (var line in source) AppendToStep(line);
    }

    /// <summary>把一行输出分发到它所属的子项行。</summary>
    public void AppendToStep(TerminalLine line)
    {
        // 未归属到任何子项的行（命令行回显、退出码汇总等）不硬塞给某个子项 ——
        // 它们仍然出现在下方终端区，所以不会"看不见的输出"。
        if (string.IsNullOrEmpty(line.Step)) return;

        var limit = _debugMode() ? int.MaxValue : StepTailLimit;
        foreach (var row in Rows)
        {
            if (!string.Equals(row.Child.Title, line.Step, StringComparison.Ordinal)) continue;
            row.Append(line, limit);
            return;
        }
    }

    /// <summary>摘掉对旧任务的订阅（页面卸载 / 切换任务时调用）。</summary>
    public void Detach() => DetachChildren();

    /// <summary>子项集合变化（安装执行器规划完成后才把子项加进来）→ 重建子项行。</summary>
    public event NotifyCollectionChangedEventHandler? ChildrenChanged;

    private void AttachChildren(TaskModel? task)
    {
        if (ReferenceEquals(_childHost, task)) return;
        DetachChildren();
        if (task is null) return;

        // 子项是**异步**出现的：执行器先建父任务，PlanAsync 之后才 Add 子项。
        // 因此必须订阅 Children 的变化，否则"先看到任务、后看到子项"这一步界面不会更新。
        task.Children.CollectionChanged += OnChildrenChanged;
        _childHost = task;
    }

    private void DetachChildren()
    {
        if (_childHost is null) return;
        _childHost.Children.CollectionChanged -= OnChildrenChanged;
        _childHost = null;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild(_childHost);
        ChildrenChanged?.Invoke(this, e);
    }

    private StepRow? RowFor(TaskModel child)
    {
        foreach (var row in Rows)
            if (ReferenceEquals(row.Child, child)) return row;
        return null;
    }

    private static bool ContainsChild(TaskModel task, TaskModel child)
    {
        foreach (var item in task.Children)
            if (ReferenceEquals(item, child)) return true;
        return false;
    }
}
