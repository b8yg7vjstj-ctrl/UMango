using System;
using System.Windows;
using System.Windows.Input;
using UMango.App.ViewModels;

namespace UMango.App.Views;

/// <summary>
/// 终端窗口：输入命令 → 流式输出。
///
/// <para>
/// 为什么是独立窗口而不是工具页里的一个子页：工具页的子页由 Shell 的
/// <c>LeftMenu</c> 驱动，加一项会同时改变 Avalonia 壳的左栏（项目约定只开发 WPF）。
/// 独立窗口不动共享层的任何导航契约，从进度页的「终端」按钮打开即可。
/// </para>
/// </summary>
public partial class TerminalWindow : Window
{
    private readonly TerminalViewModel _viewModel = new();

    public TerminalWindow()
    {
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.LineAppended += ScrollToEnd;
        Loaded += (_, _) => CommandBox.Focus();
    }

    /// <summary>回车执行；命令执行中不重复触发。</summary>
    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (_viewModel.RunCommand.CanExecute(null)) _viewModel.RunCommand.Execute(null);
    }

    /// <summary>滚到最新一行（新输出到达时）。</summary>
    private void ScrollToEnd()
    {
        // 排到布局之后：刚加入的行此时还没测量，直接 ScrollToEnd 会停在旧行上。
        Dispatcher.BeginInvoke(new Action(() => OutputScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);
    }
}
