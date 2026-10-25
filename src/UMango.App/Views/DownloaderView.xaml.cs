using System.Windows;
using System.Windows.Controls;
using UMango.App.Services;
using UMango.App.ViewModels;
using UMango.Core.Download;
using UMango.Shell.Services;
using UMango.Shell.ViewModels;

namespace UMango.App.Views;

/// <summary>
/// 下载器页（对应 PCL-CE 的「百宝箱」下载器）。
/// 参数less 构造：外壳直接 new 出来放进内容区。
/// </summary>
public partial class DownloaderView : UserControl
{
    public DownloaderView()
    {
        InitializeComponent();
        // 服务从组合根取（引擎注册表已在 AppServices 里懒加载）
        DataContext = new DownloaderViewModel(new DownloaderSession(AppServices.DownloadEngines));
    }

    /// <summary>
    /// 打开系统文件夹选择对话框，把结果写回输出目录。
    ///
    /// 对话框是 WPF 类型，所以只能留在视图层；ViewModel 里不放任何 WPF 引用。
    /// </summary>
    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = (DownloaderViewModel)DataContext;
        var host = new WpfFileDialogHost(Window.GetWindow(this));
        var selected = SystemDialogs.SelectFolder(host, null, viewModel.OutputDirectory);
        if (!string.IsNullOrEmpty(selected))
            viewModel.OutputDirectory = selected;
    }
}
