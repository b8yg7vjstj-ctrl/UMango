using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.App.Tasks;
using UMango.Core.Terminal;
using UMango.Shell.Services;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 一个子项（下载方式/阶段）对应的终端输出分组。
/// 界面把 <see cref="Lines"/> 渲染在对应子项的中文进度**同级**位置，
/// 而不是另开一个黑底终端面板（需求原文的要求）。
/// </summary>
public sealed class TerminalGroup
{
    public TerminalGroup(string step) => Step = step;

    /// <summary>分组名 = 子项标题（如「下载」「解析依赖」）；不属于任何子项的归到"其他输出"。</summary>
    public string Step { get; }

    public ObservableCollection<TerminalLine> Lines { get; } = [];
}

/// <summary>
/// 进度页：任务列表（父任务 + 按下载方式的子项进度）+ 选中任务的终端原生回传。
///
/// 三个关键点：
///   1. <see cref="Tasks"/> 直接绑定 <see cref="TaskCenter.Tasks"/>，任务由安装执行器创建，
///      本页不复制一份数据，避免"进度页和实际执行不同步"；
///   2. <see cref="TerminalLines"/> / <see cref="TerminalGroups"/> 是**订阅**出来的：
///      切换选中任务时先退订旧通道再订阅新通道（见 <see cref="OnSelectedTaskChanged"/>）；
///   3. CLI 输出回调来自后台线程，所有集合改动都经 <see cref="UiDispatch"/> 回到 UI 线程。
/// </summary>
public partial class ProgressViewModel : ObservableObject
{
    /// <summary>不属于任何子项的输出（命令行回显、退出码等）统一挂在这一组，排在子项之后。</summary>
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    public const string UnattributedStepKey = "Progress.UnattributedStep";

    /// <summary>任务列表。直接绑定 Core 的任务中心，子项进度（Children）由模板递归渲染。</summary>
    public ObservableCollection<TaskModel> Tasks { get; } = TaskCenter.Tasks;

    private TaskModel? _selectedTask;

    /// <summary>
    /// 当前选中的任务。
    /// 这里手写属性而不用 [ObservableProperty]：源生成器会为 _selectedTask 预留一个
    /// private 的 partial 钩子 OnSelectedTaskChanged(TaskModel?)，而规格要求这个方法是 public
    /// （页面在代码里切换任务时要能直接调用），两者同名会造成签名冲突。
    /// </summary>
    public TaskModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value)) OnSelectedTaskChanged(value);
        }
    }

    /// <summary>选中任务对应的终端输出（时间正序）。</summary>
    public ObservableCollection<TerminalLine> TerminalLines { get; } = [];

    /// <summary>选中任务的终端输出，按子项分组（用于把输出渲染到各子项下方）。</summary>
    public ObservableCollection<TerminalGroup> TerminalGroups { get; } = [];

    /// <summary>取消/清空等操作的即时反馈。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// 调试模式：读写 Core 的静态开关（<see cref="TerminalBridge.DebugMode"/>）。
    /// 打开后 Core 会保留更多历史行，界面上也把终端输出与子项进度同级展开。
    /// 写入时持久化，下次启动仍然是开着的。getter 直接读静态开关（唯一权威），
    /// 所以设置页改了这里也能读到最新值。
    /// </summary>
    public bool DebugMode
    {
        get => TerminalBridge.DebugMode;
        set
        {
            if (TerminalBridge.DebugMode == value) return;
            AppSettings.DebugMode = value;   // 同时落盘
            OnPropertyChanged();
        }
    }

    /// <summary>当前订阅的通道（= 持有终端输出的那个任务的标题）。</summary>
    private TerminalBridge? _bridge;

    /// <summary>分组顺序的参照任务组（选中的是子项时，指向它的父任务）。</summary>
    private TaskModel? _ownerTask;

    /// <summary>选中子项时的输出过滤：只显示该子项自己的输出；选中任务组时为 null（全部显示）。</summary>
    private Func<TerminalLine, bool>? _lineFilter;

    private readonly Dictionary<string, TerminalGroup> _groupIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// 切换选中任务：**必须先退订再订阅**。
    ///
    /// 为什么这一点是硬要求：所有任务的终端通道都挂在 <see cref="TerminalBridge"/> 的
    /// 静态通道表里，事件委托一旦订阅就常驻。如果切换任务时只订阅不退订，
    /// 上一个任务（尤其还在后台跑的任务）后续产生的每一行都会继续推进本页的集合 ——
    /// 表现为"当前任务的终端里混进了别的任务的输出"（串台），而且行数越多越难查。
    /// </summary>
    public void OnSelectedTaskChanged(TaskModel? t)
    {
        DetachBridge();

        _ownerTask = t is null ? null : OwnerOf(t);
        _lineFilter = BuildLineFilter(t, _ownerTask);
        var channelId = _ownerTask?.Title;

        if (channelId is null)
        {
            RefreshTerminalView();
            return;
        }

        _bridge = TerminalBridge.For(channelId);
        _bridge.LineAppended += HandleLineAppended;
        _bridge.Cleared += HandleCleared;

        // 订阅之前已经产生的行要一次性补齐，否则"切走再切回"会看到空白。
        RefreshTerminalView();
    }

    /// <summary>
    /// 按子项分组取终端输出：界面把每组的输出渲染在该子项下方，与中文进度同级。
    ///
    /// 两种调用姿势都支持：
    ///   · 传任务组 → 按子项顺序返回多组（最后一组是不属于任何子项的输出）；
    ///   · 传子项 → 只返回该子项这一组。
    /// 数据取自 <see cref="TerminalBridge"/> 本身而不是界面集合，
    /// 因此也可以给"非当前选中"的任务取输出（纯函数，不改界面状态）。
    /// </summary>
    public IReadOnlyList<(string Step, IReadOnlyList<TerminalLine> Lines)> GroupedOutput(TaskModel task)
    {
        var result = new List<(string Step, IReadOnlyList<TerminalLine> Lines)>();
        if (task is null) return result;

        var owner = OwnerOf(task);
        var all = TerminalBridge.For(owner.Title).Lines;

        // 传进来的是子项：只给这一步的输出。
        if (!ReferenceEquals(owner, task))
        {
            var mine = all.Where(l => string.Equals(l.Step, task.Title, StringComparison.Ordinal)).ToList();
            if (mine.Count > 0) result.Add((task.Title, mine));
            return result;
        }

        // 任务组没有任何子项（简单任务 / 规划失败）：全部输出算一组，至少还能看到原文。
        if (owner.Children.Count == 0)
        {
            if (all.Count > 0) result.Add((owner.Title, all));
            return result;
        }

        // 任务组：按子项顺序逐项取输出，顺序与上方的子项进度一一对应。
        foreach (var child in task.Children)
        {
            var lines = all.Where(l => string.Equals(l.Step, child.Title, StringComparison.Ordinal)).ToList();
            if (lines.Count > 0) result.Add((child.Title, lines));
        }

        // 兜底：命令行回显、退出码汇总等没有绑定到具体子项的行。
        var rest = all.Where(l =>
            string.IsNullOrWhiteSpace(l.Step)
            || !task.Children.Any(c => string.Equals(c.Title, l.Step, StringComparison.Ordinal))).ToList();
        if (rest.Count > 0) result.Add((L("Progress.UnattributedStep"), rest));

        return result;
    }

    /// <summary>清空选中任务的终端输出（Core 侧缓冲与界面集合一起清）。</summary>
    [RelayCommand]
    private void ClearTerminal()
    {
        if (_selectedTask is null || _ownerTask is null)
        {
            StatusMessage = L("Progress.Error.NeedTask");
            return;
        }

        // 先清 Core 的缓冲：否则重新选中该任务时历史行又会被补齐回来。
        TerminalBridge.For(_ownerTask.Title).Clear();

        TerminalLines.Clear();
        TerminalGroups.Clear();
        _groupIndex.Clear();

        // 只有任务已结束（不会再有 CLI 输出）时才把通道从静态表里摘掉：
        // 进行中的任务由安装执行器持有同一个桥实例，摘掉字典项只会让之后的
        // TerminalBridge.For(title) 拿到一个新对象，后续输出反而丢了。
        if (_selectedTask.State is not TaskState.Running and not TaskState.Waiting)
            TerminalBridge.Remove(_ownerTask.Title);

        StatusMessage = L("Progress.Cleared");
    }

    /// <summary>取消任务（参数缺省时用选中项）。</summary>
    [RelayCommand]
    private void CancelTask(TaskModel? task)
    {
        var target = task ?? SelectedTask;
        if (target is null)
        {
            StatusMessage = L("Progress.Error.NeedCancelTask");
            return;
        }

        if (TryCancel(target))
        {
            StatusMessage = L("Progress.CancelRequested", target.Title);
            return;
        }

        // 如实告知而不是静默失败：TaskModel.Cancel 只有在创建时带了取消回调才可用，
        // 而安装执行器创建的父子任务目前都没有挂回调（取消由它自己托管）。
        StatusMessage = L("Progress.NotCancelable", target.Title);
    }

    private static bool TryCancel(TaskModel task)
    {
        if (task.Cancel.CanExecute(null))
        {
            task.Cancel.Execute(null);
            return true;
        }

        // 父任务不可取消时退一步试子项：子项各自持有自己的取消回调。
        var any = false;
        foreach (var child in task.Children)
            if (TryCancel(child)) any = true;
        return any;
    }

    private void DetachBridge()
    {
        if (_bridge is null) return;
        _bridge.LineAppended -= HandleLineAppended;
        _bridge.Cleared -= HandleCleared;
        _bridge = null;
    }

    /// <summary>CLI 的输出回调来自线程池线程 → 必须切回 UI 线程再动界面集合。</summary>
    private void HandleLineAppended(TerminalLine line) => UiDispatch.Post(() =>
    {
        if (_lineFilter is null || _lineFilter(line)) AppendLine(line);
    });

    private void HandleCleared() => UiDispatch.Post(() =>
    {
        TerminalLines.Clear();
        TerminalGroups.Clear();
        _groupIndex.Clear();
    });

    /// <summary>用当前通道的全部历史行重建界面集合（首次订阅 / 切换任务时用）。</summary>
    private void RefreshTerminalView()
    {
        TerminalLines.Clear();
        TerminalGroups.Clear();
        _groupIndex.Clear();

        if (_bridge is null) return;
        foreach (var line in _bridge.Lines)
            if (_lineFilter is null || _lineFilter(line))
                AppendLine(line);
    }

    private void AppendLine(TerminalLine line)
    {
        TerminalLines.Add(line);

        var step = string.IsNullOrWhiteSpace(line.Step) ? L("Progress.UnattributedStep") : line.Step!;
        if (_groupIndex.TryGetValue(step, out var existing))
        {
            existing.Lines.Add(line);
            return;
        }

        var group = new TerminalGroup(step);
        group.Lines.Add(line);
        _groupIndex[step] = group;

        // 只在出现新分组时重排（一次任务最多「子项数 + 1」次）：
        // 分组顺序必须跟随子项顺序，终端输出才能和上方的中文进度对齐；
        // 若按到达先后追加，分组的先后会随 CLI 的换行时机乱跳。
        // OrderBy 是稳定排序，同序号的分组保持插入顺序。
        var ordered = _groupIndex.Values.OrderBy(OrderOf).ToList();
        TerminalGroups.Clear();
        foreach (var item in ordered) TerminalGroups.Add(item);
    }

    /// <summary>分组在界面上的排序号：等于它在任务子项里的位置，非子项输出排最后。</summary>
    private int OrderOf(TerminalGroup group)
    {
        var children = _ownerTask?.Children;
        if (children is null) return int.MaxValue;

        for (var i = 0; i < children.Count; i++)
            if (string.Equals(children[i].Title, group.Step, StringComparison.Ordinal))
                return i;

        return int.MaxValue;
    }

    /// <summary>选中子项时只接受该子项这一阶段的输出；选中任务组时全部都收。</summary>
    private static Func<TerminalLine, bool>? BuildLineFilter(TaskModel? task, TaskModel? owner)
    {
        if (task is null || owner is null) return null;
        if (ReferenceEquals(task, owner)) return null;

        var step = task.Title;
        return line => string.Equals(line.Step, step, StringComparison.Ordinal);
    }

    /// <summary>
    /// 找到真正持有终端通道的那个任务。
    ///
    /// Core 的安装执行器是按**父任务标题**建通道的（TerminalBridge.For(task.Title)），
    /// 子项自己没有通道；因此用户点开一个子项时，必须往上找到它所属的任务组，
    /// 否则界面会是一片空白（For("下载") 只会新建一个空通道）。
    /// </summary>
    private static TaskModel OwnerOf(TaskModel task)
    {
        if (task.Children.Count > 0) return task;

        var parent = TaskCenter.Tasks.FirstOrDefault(t => t.Children.Contains(task));
        return parent ?? task;
    }
}
