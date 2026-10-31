using System.Windows;
using System.Windows.Controls;
using UMango.App.Localization;
using UMango.Shell.Services;
using UMango.Shell.ViewModels;

namespace UMango.App.Views;

/// <summary>
/// 工具/诊断页：系统诊断 / 合规与许可 / 关于。
///
/// 代码后置里没有业务逻辑，只做三件事：取服务、建自己的诊断 ViewModel、Loaded 时 await 异步加载。
/// 为什么这一页的加载必须失败可见：它是"出问题时的第一站"——
/// 如果诊断过程本身出错却静默（比如探测抛异常被吞掉），用户看到的是一片空白，
/// 反而比没有这一页更糟；所以本页所有异常都会渲染成可读的中文原因。
/// </summary>
public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        ViewModel = new ToolsDiagnosticsViewModel();
        Actions = new ToolsActionsViewModel(AppServices.DownloadEngines);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = ViewModel;

        // Loaded 里 await 异步接口：构造函数不能 await，而在 UI 线程上 .Result/.Wait()
        // 等 CLI 探测会死锁（WPF 的 SynchronizationContext 被阻塞）。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 本页的 ViewModel。
    ///
    /// 定义在 Shell 层（平台中立）：诊断页是排查问题的入口，
    /// WPF 壳与 Avalonia 壳必须给出**完全一致**的结论与措辞；
    /// 若两边各写一份，很快就会漂移（一边加了"未验证"标注、另一边没加）。
    /// </summary>
    public ToolsDiagnosticsViewModel ViewModel { get; }

    /// <summary>
    /// 工具动作（垃圾清理 / 自定义下载）的 ViewModel。
    ///
    /// 单独一个对象而不是塞进 <see cref="ToolsDiagnosticsViewModel"/>：诊断部分是"只读地看"，
    /// 动作部分会**改磁盘、会发网络请求**，两者的状态与生命周期不同；
    /// 混在一起会让"页面 Loaded 时到底跑了什么"难以推断。
    /// </summary>
    public ToolsActionsViewModel Actions { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 走注册表缓存（不重新起 CLI 进程）；重新探测由「重新探测」按钮显式触发。
        await ViewModel.EnsureLoadedAsync();
        SyncReportText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>
    /// 页面重新可见时，只刷新"与记录有关"的两行数字（不动包源明细、不起 CLI）。
    ///
    /// 为什么不能省：外壳用 Visibility 切页、页面只创建一次，Loaded 不会再触发；
    /// 而用户很可能是"装完一个包再回到工具页"，若不在这里补一次，
    /// 「已记录次数 / 记录文件」会停在**进入应用那一刻**的旧数字上 —— 那两行正是
    /// "次数记录"在工具页的落点，显示错的数字比不显示更糟。
    /// 走内存里的记录（不做磁盘 I/O），所以既不会卡顿，也不与「重新探测」的语义重复。
    /// </summary>
    private void ToolsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || ViewModel is null) return;   // 构造期（尚未挂 ViewModel）直接忽略

        try
        {
            ViewModel.RefreshRecordsView();
        }
        catch (Exception ex)
        {
            // 与其它 UI 事件处理器同理：异常漏出去会终止进程，这里退化成一行可读提示。
            ViewModel.StatusMessage = ViewText.L("Settings.Records.RefreshFailed", ex.Message);
        }
    }

    /// <summary>
    /// 诊断报告是多行原文（含动态生成的对照表），刻意**不用绑定**而在这里赋值。
    /// 为什么：报告正文由 ViewModel 逐字符计算，绑定只会在每次属性变更时重算一遍；
    /// 而报告只在"加载完成/重新探测完成"这两个时刻需要刷新，显式同步更省也更直观。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolsDiagnosticsViewModel.DiagnosticsText)) SyncReportText();
    }

    private void SyncReportText() => DiagnosticsReportBox.Text = ViewModel.DiagnosticsText;
}
