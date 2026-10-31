// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧下载页 —— 与 WPF 版（`src/UMango.App/Views/DownloadView.xaml.cs`，368 行）同构。
//
// 页面职责边界（为什么这样切分）：
//   · 所有业务都在 `DownloadViewModel`（Shell 层，平台中立）里：探测、搜索、安装、错误文案翻译；
//     页面只做 XAML 表达不了的事：
//       1. 空态/初始提示/已安装空态的显隐合成（判定跨多个属性，绑定表达不了）；
//       2. 回车触发搜索、按钮点击时记录"已搜过"。
//   · 左栏选中项**不需要**页面代码：WPF 那边靠 ItemsControl + MultiBinding 比较引用，
//     Avalonia 侧用 ListBox 的 SelectedItem 双向绑定，选中态由 `:selected` 伪类渲染。
//     这是两壳唯一的结构性差异，不是漏移植。
//
// 线程约定：页面没有任何同步等待（.Result/.Wait()），首次加载与所有命令都 await 到异步接口，
// CLI 跑到线程池上，UI 线程不会被卡住。

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>下载页：左栏包源分类 + 右栏搜索与结果卡片。</summary>
public partial class DownloadView : UserControl
{
    /// <summary>
    /// 是否已经发起过一次搜索。
    ///
    /// <para>
    /// 为什么需要：需求要求"结果为空**且已搜索过**"才显示空态 ——
    /// 一进页面就显示"没有找到匹配的包"会让人以为搜索失败了；
    /// 这个状态只存在于界面层，故不放进 ViewModel。
    /// </para>
    /// </summary>
    private bool _searchAttempted;

    /// <summary>本页自己的 ViewModel（外壳统一用 <c>new DownloadView()</c> 创建，故构造必须无参）。</summary>
    public DownloadViewModel ViewModel { get; }

    public DownloadView()
    {
        InitializeComponent();

        ViewModel = new DownloadViewModel();
        DataContext = ViewModel;

        // 空态判定跨了多个来源（结果集合、是否搜过、忙碌/错误状态、视图模式），
        // 单一绑定表达不了，因此在这里合成；订阅通知而不是在每次操作后手动刷新，
        // 才不会漏掉 ViewModel 内部的变化。
        ViewModel.Results.CollectionChanged += OnResultsChanged;
        ViewModel.Installed.CollectionChanged += OnResultsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 先把提示摆成初始状态，避免出现一帧的错位。
        UpdateHintVisibility();

        // 首次进入才做包源探测（要起 npm/pnpm/git 进程），故在 Loaded 里 await。
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 只初始化一次：页面在外壳里可能被反复卸载/挂载，而"重新探测 CLI"不该每次切页都做。
        // ViewModel.InitializeAsync 本身也是幂等的，这里取消订阅只是省掉重复调用。
        Loaded -= OnLoaded;

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception)
        {
            // InitializeAsync 内部已把失败写进 ErrorMessage（界面会显示错误条 + 建议）；
            // 这里是 async void 的兜底：异常直接冒到 Dispatcher 会终结整个应用。
        }

        UpdateHintVisibility();
    }

    // ===== 界面事件的两个收口 =====

    /// <summary>搜索框回车 = 点「搜索」。</summary>
    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // 标记为已处理：避免回车继续冒泡触发别的默认行为（例如把焦点交给某个按钮）。
        e.Handled = true;

        _searchAttempted = true;
        UpdateHintVisibility();

        // 命令自身带并发保护（执行期间 CanExecute 为 false），这里再判一次免得白跑。
        if (ViewModel.SearchCommand.CanExecute(null))
        {
            ViewModel.SearchCommand.Execute(null);
        }
    }

    /// <summary>
    /// 「搜索」与错误条上的「重试」共用。
    ///
    /// <para>
    /// 命令绑定在 XAML 上（Button.Command），这里只负责记录"用户搜过了"，
    /// 让空态在搜索结束（结果与状态通知到达）时正确显示。
    /// </para>
    /// </summary>
    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        _searchAttempted = true;
        UpdateHintVisibility();
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHintVisibility();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换包源 = 上一轮的"搜过"作废：ViewModel 切换分类时会清空 Results，
        // 若不清掉这个标记，右栏会显示"没有找到匹配的包" ——
        // 而实际上在新包源里一次都还没搜过。
        if (e.PropertyName == nameof(DownloadViewModel.SelectedCategory))
        {
            _searchAttempted = false;
        }

        UpdateHintVisibility();
    }

    /// <summary>
    /// 合成三块提示的显隐。三者互斥：
    ///   · 搜过 + 无结果 + 不在忙碌 + 没有错误 → 空态文案；
    ///   · 没搜过 + 无结果                     → 初始提示；
    ///   · 忙碌（正在搜索）或出错               → 两者都不显示（由进度条/错误条接管）；
    ///   · 已安装模式 + 列表为空 + 不在忙碌     → 已安装空态（说明为什么是空的）。
    /// </summary>
    private void UpdateHintVisibility()
    {
        var noResults = ViewModel.Results.Count == 0;

        // 搜索空态只属于"搜索结果"模式：切到已安装列表后必须收起，
        // 否则会出现"已安装列表是空的，却叠着『没有找到匹配的包』"的错位提示。
        var searchMode = !ViewModel.IsInstalledMode;

        EmptyState.IsVisible = searchMode
                               && noResults
                               && _searchAttempted
                               && !ViewModel.IsBusy
                               && string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);

        IdleHint.IsVisible = searchMode && noResults && !_searchAttempted;

        // WPF 用 MultiDataTrigger 组合这三个条件；Avalonia 无触发器，
        // 且与上面两块提示同源判定，放一处才不会三处各判一半。
        InstalledEmptyHint.IsVisible = ViewModel.IsInstalledMode
                                       && ViewModel.Installed.Count == 0
                                       && !ViewModel.IsBusy;
    }
}
