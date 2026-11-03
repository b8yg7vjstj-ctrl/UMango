using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UMango.App.Services;
using UMango.Shell.ViewModels;
using UMango.Core.App.Tasks;
using UMango.Core.Terminal;
using UMango.Core.Localization;
using UMango.App.Localization;
using UMango.Shell.Services;

namespace UMango.App.Views;


/// <summary>
/// 「0~1 分数 → 0~100 百分比」转换器。
///
/// 为什么需要它：Core 的 <see cref="TaskModel.Progress"/> 是 0~1 的分数
/// （<see cref="TaskCenter.Complete"/> 里直接写 1.0），而界面上的进度条按百分比刻度展示
/// （Maximum=100，看得到"62%"这种直觉数值）。换算是纯数值计算，与主题无关，
/// 所以在页面资源里用 StaticResource 引用 —— 只有颜色/尺寸 token 才必须走 DynamicResource。
/// </summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double fraction ? Math.Clamp(fraction, 0d, 1d) * 100d : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;   // 进度条是只读展示，不需要回写
}

/// <summary>
/// 「子项（TaskModel） → 视图侧的子项行（<see cref="StepRow"/>）」转换器。
///
/// 用法（见 ProgressView.xaml 的 ChildStepTemplate）：
///   MultiBinding = [当前子项, 本页的 <see cref="ProgressView.StepRows"/>]
/// 外层列表直接绑定 SelectedTask.Children（数据源是 Core 的子项），
/// 模板内部需要的"这一行的输出 / 省略提示"则来自 StepRows 里对应的那一行 ——
/// 转换器就是这两者之间的桥。
///
/// 找不到对应行时**就地补建一行并登记**：容器生成是异步的（先有子项、后有模板实例），
/// 极端时序下模板可能先于视图同步被实例化；补建能保证子项进度行不会渲染成空白。
/// </summary>
public sealed class StepRowSourceConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not TaskModel child) return null;

        // 页面根节点还没接上（AncestorType 暂时解析不到）时退化成"临时行"：
        // 子项进度（进度条/中文名/百分比/状态）照常显示，只是这一轮的输出块为空，
        // 而下方终端区始终是全量的 —— 界面不会因为一次绑定时序问题而少显示一行进度。
        if (values[1] is not ObservableCollection<StepRow> rows) return new StepRow(child);

        foreach (var row in rows)
            if (ReferenceEquals(row.Child, child)) return row;

        var created = new StepRow(child);
        rows.Add(created);
        return created;
    }

    /// <summary>本绑定是单向的（只用于设置模板的 DataContext），不需要回写；
    /// 返回 Binding.DoNothing 而不是抛异常，避免任何意外的回写路径把应用打断。</summary>
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [Binding.DoNothing, Binding.DoNothing];
}

/// <summary>
/// 进度页（Views/ProgressView.xaml 的代码层）。
///
/// 职责边界：业务数据（任务、子项、进度、状态、终端行缓冲）全部来自
/// <see cref="ProgressViewModel"/> 与 Core；本文件只做两件视图层的事：
///   1. **把终端输出按子项分发**，让每个子项的输出渲染在它自己那一行下方
///      （需求原文「与子中文进度同级」的落地）；
///   2. **按调试模式裁剪显示行数**（关闭时只看最近若干行 + 省略提示，打开时全量）。
///
/// 所有集合操作都在 UI 线程：ViewModel 已经把 Core 的回调经 UiDispatch 切回 UI 线程，
/// 本文件只订阅 ViewModel 的集合事件，因此天然在 UI 线程上。
/// </summary>
public partial class ProgressView : UserControl
{
    /// <summary>非调试模式下终端区最多显示的行数（需求建议值：最后 50 行）。</summary>
    private const int TerminalTailLimit = 50;

    /// <summary>非调试模式下每个子项最多显示的行数。比终端区更少，保证"子项进度"一屏能看全。</summary>
    private const int StepTailLimit = 8;

    /// <summary>距底部多少像素以内视为"用户还贴着底部看"，用于决定是否自动跟随滚动。</summary>
    private const double PinnedThreshold = 24;

    private readonly ProgressViewModel _vm = new();
    private readonly ObservableCollection<TerminalLine> _terminalTail = [];

    /// <summary>
    /// 每个子项一行（含它自己的输出）。
    ///
    /// 这个集合是**公开**的：XAML 里的 ChildStepTemplate 通过
    /// {RelativeSource AncestorType=ProgressView} 拿到它，把子项映射成行（见 StepRowSourceConverter）。
    /// 行对象一旦建好就不再替换 —— 模板持有的是行实例，替换会让输出断流。
    /// </summary>
    public ObservableCollection<StepRow> StepRows => _vm.StepMirror.Rows;

    private bool _hooked;
    private bool _terminalPinned = true;

    /// <summary>是否已有一个"滚到底"的投递在排队（避免高频输出时把 Dispatcher 队列刷爆）。</summary>
    private bool _scrollPending;

    /// <summary>上一次按哪个调试模式重建了显示行数（用于"从别的页面切回来"时判断要不要重建）。</summary>
    private bool _lastDebugMode;

    /// <summary>是否已经做过一次完整初始化（订阅 + 两侧镜像重建）。</summary>
    private bool _initialized;

    /// <summary>已打开的终端窗口（再次点击时复用，而不是无限开新窗口）。</summary>
    private Views.TerminalWindow? _terminalWindow;

    /// <summary>终端输出区的过滤视图（只影响显示，不改 _terminalTail 内容）。</summary>
    private ListCollectionView _terminalView = null!;

    /// <summary>终端输出过滤：按关键字包含匹配（大小写不敏感）。</summary>
    private void TerminalFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = (TerminalFilterBox.Text ?? "").Trim();

        _terminalView.Filter = keyword.Length == 0
            ? null
            : item => item is TerminalLine line
                      && line.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        _terminalView.Refresh();
    }

    /// <summary>
    /// 导出当前任务的完整终端输出到桌面。
    ///
    /// 取的是 TerminalBridge 的完整行（而不是界面上截断后的 _terminalTail）——
    /// 导出是为了排障，缺行就失去意义。
    /// </summary>
    private void ExportTerminal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var task = _vm.SelectedTask;
            var lines = task is null
                ? _vm.TerminalLines.ToList()
                : UMango.Core.Terminal.TerminalBridge.For(task.Title).Lines.ToList();

            var directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(directory, $"umango-terminal-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            System.IO.File.WriteAllLines(path, lines.Select(l => l.Text));

            _vm.StatusMessage = UMango.Core.Localization.LocalizationService.Text("Terminal.Exported", path);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = UMango.Core.Localization.LocalizationService.Text("Terminal.ExportFailed", ex.Message);
        }
    }

    /// <summary>
    /// 打开独立终端窗口。
    ///
    /// 为什么不做成工具页的子页：那需要往 Shell 的 LeftMenu 里加一项，
    /// 而左栏是两个壳共用的 —— 会连带改变 Avalonia 壳的菜单（项目约定只开发 WPF）。
    /// </summary>
    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_terminalWindow is { IsLoaded: true })
        {
            _terminalWindow.Activate();
            return;
        }

        _terminalWindow = new Views.TerminalWindow { Owner = Window.GetWindow(this) };
        _terminalWindow.Closed += (_, _) => _terminalWindow = null;
        _terminalWindow.Show();
    }

    public ProgressView()
    {
        InitializeComponent();

        // 页面自持 ViewModel（外壳只 new 一个无参的页面，不关心 ViewModel 从哪来）。
        DataContext = _vm;

        // 终端区的数据源在构造时挂上：集合引用固定，之后只改内容不改引用，
        // 这样清空/切任务/改调试模式都不会重建控件树。
        // 套一层 ListCollectionView 是为了支持过滤（过滤只影响显示，不影响 _terminalTail 本身）。
        _terminalView = new ListCollectionView(_terminalTail);
        TerminalList.ItemsSource = _terminalView;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ================================================================
    // 生命周期
    // ================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 本页唯一需要的"加载"：服务初始化会读设置文件与记录文件（同步文件 IO），
        // 丢到线程池执行，避免首帧被磁盘 IO 拖住。
        // 任务数据不需要加载 —— 它直接绑定 TaskCenter.Tasks 这一份内存集合。
        await Task.Run(AppServices.Initialize);

        EnsureInitialized();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

    /// <summary>
    /// 页面变可见时对齐全局调试开关并确保数据订阅在位。
    ///
    /// 为什么需要（两条都是外壳的宿主方式决定的）：
    ///   · 外壳把五个页面**叠放**、只切 Visibility（实例一直挂在可视树上），所以 Loaded 只来一次；
    ///     万一本页一开始就是 Collapsed、Loaded 没触发，也必须能在这里补上初始化；
    ///   · 调试模式是 Core 的**静态**开关，设置页也有一个入口，
    ///     从设置页改了开关再切回本页时，开关显示与"显示多少行"都必须是新的。
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        // 把绑定从源头（ViewModel.DebugMode → TerminalBridge.DebugMode）重新读一次。
        DebugToggleButton.GetBindingExpression(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();

        EnsureInitialized();
        SyncDebugModeDisplay();
    }

    /// <summary>
    /// 幂等初始化：订阅数据、按当前选中任务重建两侧镜像。
    /// 首次之后的重复调用只保留 Hook（廉价），不做几千行的重建。
    /// </summary>
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProgressViewModel.SelectedTask):
                // 注意事件顺序：ViewModel 的 SelectedTask setter 先触发 PropertyChanged，
                // 之后才在 OnSelectedTaskChanged 里清空并补齐 TerminalLines。
                // 这里先按新任务的子项重建（此刻可能还残留上一个任务的行），
                // 紧随其后的 Clear（Reset）会再重建一次，最终状态一定正确。
                RebuildStepRows();
                RebuildTerminal();
                break;

            case nameof(ProgressViewModel.DebugMode):
                // 调试模式决定"显示多少行"，切换后必须整体重建，否则打开调试模式也看不到早先被裁掉的行。
                SyncDebugModeDisplay();
                break;
        }
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 只有"当前什么都没选中"时才自动选中新任务。
                // 为什么不在每次新任务出现时都抢选中：用户可能正盯着另一个任务的终端输出，
                // 画面突然跳到别的任务上，看起来就像终端"串台"了。
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
            // 清空 / 切换任务时的"先清后补" → 两侧都整体重建。
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

        // 自动选中是"替用户做的选择"，顺手把它滚进可视区（列表可能已经很长）。
        // 手动点选时不做这件事：用户点的项本来就在视野里。
        // 页面此刻不可见（外壳把本页 Collapsed 着）时跳过：容器还没生成，滚了也没有意义。
        if (TaskList.IsVisible) TaskList.ScrollIntoView(newest);
    }

    // ================================================================
    // 子项行 + 挂在子项下的输出
    //
    // 全部委派给 Shell 的 StepRowMirror —— WPF 壳与 Avalonia 壳共用同一份实现，
    // 避免两套壳的进度页行为漂移（需求里的「降低重复代码率」）。
    // ================================================================

    /// <summary>重建子项行（补新的、摘旧的、顺序对齐）。</summary>
    private void RebuildStepRows() => _vm.StepMirror.Rebuild(_vm.SelectedTask);

    /// <summary>整体重建每行的输出（清空后重放当前源集合）。</summary>
    private void RebuildStepOutputs() => _vm.StepMirror.RebuildOutputs(_vm.TerminalLines);

    /// <summary>把一行输出分发到它所属的子项行。</summary>
    private void AppendToStep(TerminalLine line) => _vm.StepMirror.AppendToStep(line);

    /// <summary>摘掉对旧任务子项集合的订阅。</summary>
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
    /// 省略提示：按"源集合行数 − 当前显示行数"算被省略了多少行。
    /// 关闭调试模式时明确告诉用户"上面还有内容"，否则会被误认为 CLI 只输出了这么点。
    /// </summary>
    private void UpdateTerminalHint()
    {
        var omitted = _vm.TerminalLines.Count - _terminalTail.Count;
        if (omitted <= 0 || _vm.DebugMode)
        {
            TerminalTruncationHint.Visibility = Visibility.Collapsed;
            return;
        }

        TerminalTruncationHint.Text =
            ViewText.L("Progress.OmittedTail", TerminalTailLimit, _vm.TerminalLines.Count);
        TerminalTruncationHint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 自动跟随最新输出。
    /// 三条细节：
    ///   · 用户手动往上翻时不再自动滚（否则想回看历史输出会被一直拽回底部）；
    ///   · 必须等布局完成再滚：集合刚变更时 ScrollableHeight 还是旧值，立刻 ScrollToEnd 会滚不到位，
    ///     所以用 Background 优先级投递，让渲染（Render 优先级更高）先把新行排版好；
    ///   · 同一时刻只允许一个"滚到底"排队：CLI 输出可能是每秒上百行，
    ///     每行都投递一次会把 Dispatcher 队列刷满、反而让界面卡顿。
    /// </summary>
    private void ScrollTerminalToEnd()
    {
        if (!_terminalPinned || _scrollPending) return;
        _scrollPending = true;

        Dispatcher.InvokeAsync(() =>
        {
            _scrollPending = false;
            TerminalScroll.UpdateLayout();
            TerminalScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void TerminalScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 内容变高（ExtentHeightChange != 0）造成的事件不代表用户在滚动，必须忽略：
        // 否则 CLI 高频输出时每加一行都会被判成"用户已上翻"，自动跟随会莫名其妙停掉。
        if (e.ExtentHeightChange != 0) return;

        _terminalPinned = TerminalScroll.ScrollableHeight - TerminalScroll.VerticalOffset <= PinnedThreshold;
    }
}
