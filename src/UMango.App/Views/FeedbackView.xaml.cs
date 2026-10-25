using System.Windows;
using System.Windows.Controls;

namespace UMango.App.Views;

/// <summary>
/// 反馈页。
///
/// 代码后置只做一件事：把自己的 ViewModel 装上，并在 Loaded 时触发一次加载。
/// 业务逻辑全在 <see cref="UMango.Shell.ViewModels.FeedbackViewModel"/> 里，
/// 那部分可以在 macOS 上被单元测试覆盖。
/// </summary>
public partial class FeedbackView : UserControl
{
    /// <summary>构造视图。</summary>
    public FeedbackView()
    {
        InitializeComponent();
        DataContext = new UMango.Shell.ViewModels.FeedbackViewModel();
    }

    private void FeedbackView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UMango.Shell.ViewModels.FeedbackViewModel vm)
            vm.LoadCommand.Execute(null);
    }
}
