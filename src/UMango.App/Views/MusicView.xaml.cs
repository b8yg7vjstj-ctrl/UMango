using System.Windows;
using System.Windows.Controls;

namespace UMango.App.Views;

/// <summary>
/// 音乐页。
///
/// 代码后置只做一件事：把自己的 ViewModel 装上，并在 Loaded 时触发一次曲库扫描。
/// 业务逻辑全在 <see cref="UMango.App.ViewModels.MusicViewModel"/> 与
/// <c>UMango.Core.Media.Music</c> 里，那部分可以在 macOS 上被单元测试覆盖。
/// </summary>
public partial class MusicView : UserControl
{
    /// <summary>构造视图。</summary>
    public MusicView()
    {
        InitializeComponent();
        DataContext = new UMango.App.ViewModels.MusicViewModel();
    }

    private void MusicView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UMango.App.ViewModels.MusicViewModel vm)
            vm.RefreshCommand.Execute(null);
    }
}
