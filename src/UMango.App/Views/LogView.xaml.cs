using System.Windows.Controls;
using UMango.App.ViewModels;

namespace UMango.App.Views;

/// <summary>日志页。参数less 构造：外壳直接 new 出来放进内容区。</summary>
public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContext = new LogViewModel();
    }
}
