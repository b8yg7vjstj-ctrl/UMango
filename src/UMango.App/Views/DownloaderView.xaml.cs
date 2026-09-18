using System.Windows.Controls;
using UMango.App.Services;
using UMango.App.ViewModels;
using UMango.Core.Download;

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
}
