// SPDX-License-Identifier: Apache-2.0
//
// 进度页（Avalonia 版）code-behind。
//
// 与 WPF 版 `src/UMango.App/Views/ProgressView.xaml.cs`（约 380 行）**同构**：
// 逻辑全部委派给 Shell 的 `ProgressViewModel` + `StepRowMirror`，
// 本文件只做「Avalonia 特有的三件事」：
//   1. 页面自持 ViewModel（外壳只 new 一个无参页面）；
//   2. 终端区的尾部集合（固定集合引用、只改内容），配合「贴底自动滚动」；
//   3. 生命周期订阅（Loaded/Unloaded、可见性变化）。
//
// 明确不做的事（与 WPF 版一致）：不复制任何业务数据、不自己算进度、不自己分发输出到子项。
//
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UMango.Core.Terminal;
using UMango.Shell.Services;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>
/// 进度页：左栏任务列表 + 右侧「按下载方式列明的子项进度」+ 原生回传的终端输出。
/// </summary>
public partial class ProgressView : UserControl
{
    /// <summary>非调试模式下终端区最多保留的行数。</summary>
    private const int TerminalTailLimit = 200;

    /// <summary>距底部多少像素以内视为「用户还贴着底部看」，用于决定是否自动跟随滚动。</summary>
    private const double PinnedThreshold = 24;

    private readonly ProgressViewModel _vm = new();

    /// <summary>终端区显示用的尾部集合（集合引用固定，只改内容）。</summary>
    private readonly ObservableCollection<TerminalLine> _terminalTail = [];

    private bool _hooked;
    private bool _terminalPinned = true;
    private bool _scrollPending;
    private bool _lastDebugMode;
    private bool _initialized;

    public ProgressView()
    {
        InitializeComponent();

        // 页面自持 ViewModel（外壳只 new 一个无参的页面，不关心 ViewModel 从哪来）。
        DataContext = _vm;

        // 终端区的数据源在构造时挂上：集合引用固定，之后只改内容不改引用，
        // 这样清空 / 切任务 / 改调试模式都不会重建控件树。
        TerminalList.ItemsSource = _terminalTail;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PropertyChanged += OnSelfPropertyChanged;
    }

    // ================================================================
    // 生命周期
    // ================================================================

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 本页唯一需要的「加载」：服务初始化会读设置文件与记录文件（同步文件 IO），
        // 丢到线程池执行，避免首帧被磁盘 IO 拖住。
        // 任务数据不需要加载 —— 它直接绑定 TaskCenter.Tasks 这一份内存集合。
        await Task.Run(AppServices.Initialize);

        EnsureInitialized();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => Unhook();

    /// <summary>
    /// 页面变可见时对齐全局调试开关并确保数据订阅在位。
    ///
    /// 为什么需要（两条都是外壳的宿主方式决定的）：
    ///   · 外壳把五个页面**叠放**、只切 IsVisible（实例一直挂在可视树上），所以 Loaded 只来一次；
    ///     万一本页一开始就是不可见、Loaded 没触发，也必须能在这里补上初始化；
    ///   · 调试模式是 Core 的**静态**开关，设置页也有一个入口，
    ///     从设置页改了开关再切回本页时，开关显示与「显示多少行」都必须是新的。
    /// </summary>
    private void OnSelfPropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.OldValue is false && e.NewValue is true)
        {
            EnsureInitialized();
            SyncDebugModeDisplay();
        }
    }

    /// <summary>幂等初始化：订阅数据、按当前选中任务重建两侧镜像。</summary>
    private void EnsureInitialized()
    {
        Hook();
        if (_initialized) return;
        _initialized = true;

        RebuildStepRows();
        RebuildTerminal();
        AutoSelectNewestIfIdle();
        _lastDebugMode = _vm.DebugMode;
    }

    private void SyncDebugModeDisplay()
    {
        var debug = _vm.DebugMode;
        if (debug == _lastDebugMode) return;

        _lastDebugMode = debug;
        RebuildStepRows();
        RebuildTerminal();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;

        _vm.Tasks.CollectionChanged += OnTasksChanged;
        _vm.TerminalLines.CollectionChanged += OnTerminalLinesChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;

        _vm.Tasks.CollectionChanged -= OnTasksChanged;
        _vm.TerminalLines.CollectionChanged -= OnTerminalLinesChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        DetachChildren();

        // 退订后镜像可能与数据源脱节（摘树期间照样会有输出），下次可见时整体重建一次。
        _initialized = false;
    }

    // ================================================================
    // ViewModel 事件
    // ================================================================

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProgressViewModel.SelectedTask):
                // 事件顺序：ViewModel 的 SelectedTask setter 先触发 PropertyChanged，
                // 之后才在 OnSelectedTaskChanged 里清空并补齐 TerminalLines。
                // 这里先按新任务的子项重建（此刻可能还残留上一个任务的行），
                // 紧随其后的 Clear（Reset）会再重建一次，最终状态一定正确。
                RebuildStepRows();
                RebuildTerminal();
                break;

            case nameof(ProgressViewModel.DebugMode):
                // 调试模式决定「显示多少行」，切换后必须整体重建，否则打开调试模式也看不到早先被裁掉的行。
                SyncDebugModeDisplay();
                break;
        }
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 只有「当前什么都没选中」时才自动选中新任务。
                // 为什么不在每次新任务出现时都抢选中：用户可能正盯着另一个任务的终端输出，
                // 画面突然跳到别的任务上，看起来就像终端「串台」了。
                AutoSelectNewestIfIdle();
                break;

            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Reset:
                // 选中的任务被移除了：改选最新一个，避免右侧详情空着（左侧列表还有别的任务）。
                if (_vm.SelectedTask is null || !_vm.Tasks.Contains(_vm.SelectedTask))
                    _vm.SelectedTask = _vm.Tasks.Count > 0 ? _vm.Tasks[^1] : null;
                break;
        }
    }

    private void OnTerminalLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // 清空 / 切换任务时的「先清后补」→ 两侧都整体重建。
            RebuildStepOutputs();
            RebuildTerminal();
            return;
        }

        if (e.NewItems is null) return;
        foreach (TerminalLine line in e.NewItems)
        {
            AppendToStep(line);
            AppendToTerminalTail(line);
        }
    }

    private void AutoSelectNewestIfIdle()
    {
        if (_vm.SelectedTask is not null || _vm.Tasks.Count == 0) return;

        var newest = _vm.Tasks[^1];
        _vm.SelectedTask = newest;

        // 自动选中是「替用户做的选择」，顺手把它滚进可视区（列表可能已经很长）。
        // 手动点选时不做这件事：用户点的项本来就在视野里。
        TaskList.ScrollIntoView(newest);
    }

    // ================================================================
    // 子项行 + 挂在子项下的输出
    //
    // 全部委派给 Shell 的 StepRowMirror —— WPF 壳与 Avalonia 壳共用同一份实现，
    // 避免两套壳的进度页行为漂移（需求里的「降低重复代码率」）。
    // ================================================================

    private void RebuildStepRows() => _vm.StepMirror.Rebuild(_vm.SelectedTask);

    private void RebuildStepOutputs() => _vm.StepMirror.RebuildOutputs(_vm.TerminalLines);

    private void AppendToStep(TerminalLine line) => _vm.StepMirror.AppendToStep(line);

    private void DetachChildren() => _vm.StepMirror.Detach();

    // ================================================================
    // 终端区（时间顺序的全量输出）
    // ================================================================

    private void RebuildTerminal()
    {
        _terminalTail.Clear();

        var source = _vm.TerminalLines;
        var limit = _vm.DebugMode ? int.MaxValue : TerminalTailLimit;
        var skip = Math.Max(0, source.Count - limit);
        for (var i = skip; i < source.Count; i++) _terminalTail.Add(source[i]);

        UpdateTerminalHint();
        ScrollTerminalToEnd();
    }

    private void AppendToTerminalTail(TerminalLine line)
    {
        _terminalTail.Add(line);

        var limit = _vm.DebugMode ? int.MaxValue : TerminalTailLimit;
        while (_terminalTail.Count > limit) _terminalTail.RemoveAt(0);

        UpdateTerminalHint();
        ScrollTerminalToEnd();
    }

    /// <summary>
    /// 省略提示：按「源集合行数 − 当前显示行数」算被省略了多少行。
    /// 关闭调试模式时明确告诉用户「上面还有内容」，否则会被误认为 CLI 只输出了这么点。
    /// </summary>
    private void UpdateTerminalHint()
    {
        var omitted = _vm.TerminalLines.Count - _terminalTail.Count;
        if (omitted <= 0 || _vm.DebugMode)
        {
            TerminalTruncationHint.IsVisible = false;
            return;
        }

        TerminalTruncationHint.Text =
            UMango.Core.Localization.LocalizationService.Text("Progress.OmittedTail", TerminalTailLimit, _vm.TerminalLines.Count);
        TerminalTruncationHint.IsVisible = true;
    }

    /// <summary>
    /// 自动跟随最新输出。
    /// 三条细节（与 WPF 版一致）：
    ///   · 用户手动往上翻时不再自动滚（否则想回看历史输出会被一直拽回底部）；
    ///   · 必须等布局完成再滚：集合刚变更时滚动区高度还是旧值，立刻 ScrollToEnd 会滚不到位；
    ///   · 同一时刻只允许一个「滚到底」排队：CLI 输出可能是每秒上百行，
    ///     每行都投递一次会把 UI 队列刷满、反而让界面卡顿。
    /// </summary>
    private void ScrollTerminalToEnd()
    {
        if (!_terminalPinned || _scrollPending) return;
        _scrollPending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            TerminalScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    /// <summary>用户手动滚动时更新「是否贴底」的判定。</summary>
    private void OnTerminalScrollChanged(object? sender, global::Avalonia.Controls.ScrollChangedEventArgs e)
    {
        var viewport = TerminalScroll.Viewport;
        var offset = TerminalScroll.Offset;
        if (viewport.Height <= 0) return;

        _terminalPinned = offset.Y + viewport.Height >= TerminalScroll.Extent.Height - PinnedThreshold;
    }
}
