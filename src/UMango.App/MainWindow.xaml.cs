using System.Windows;
using System.Windows.Controls;
using UMango.Core.App.Tasks;

namespace UMango.App;

/// <summary>
/// 主窗口。顶部四个 banner 选项卡 + 左侧随选项卡变化的菜单（PCL-CE 结构）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TaskList.ItemsSource = TaskCenter.Tasks;
        ShowTab("home");
        DemoSeed();   // 临时演示数据：验证「父任务 + 子项进度」的渲染
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) ShowTab(tag);
    }

    /// <summary>切换选项卡：显示对应页面并重建左栏菜单。</summary>
    private void ShowTab(string tag)
    {
        PageHome.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageDownload.Visibility = tag == "download" ? Visibility.Visible : Visibility.Collapsed;
        PageSetup.Visibility = tag == "setup" ? Visibility.Visible : Visibility.Collapsed;
        PageTools.Visibility = tag == "tools" ? Visibility.Visible : Visibility.Collapsed;

        // 左栏菜单随选项卡变化（"下方一个选项卡对应菜单"）
        string[] items = tag switch
        {
            "home" => ["概览", "管理记录"],
            "download" => ["包源", "任务队列", "已完成"],
            "setup" => ["常规", "包管理器", "界面与主题", "下载", "关于"],
            "tools" => ["系统诊断", "终端", "调试模式", "日志"],
            _ => []
        };
        LeftRail.Children.Clear();
        foreach (var item in items)
        {
            LeftRail.Children.Add(new TextBlock
            {
                Text = item,
                Height = 36,
                FontSize = 13,
                Padding = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left,
                Foreground = (System.Windows.Media.Brush)FindResource("BrC1")
            });
        }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 演示：构造一个父任务 + 三个子项（按下载方式区分），
    /// 用来验证"按不同下载方式列明子项进度"的界面结构。
    /// </summary>
    private static void DemoSeed()
    {
        var group = TaskModel.Group("安装 express（npm）");
        group.Children.Add(TaskModel.Progressive("依赖解析"));
        group.Children.Add(TaskModel.Progressive("注册表下载"));
        group.Children.Add(TaskModel.Progressive("tarball 下载"));
        group.Children.Add(TaskModel.Progressive("链接写入"));
        group.Children[0].Progress = 1.0;
        group.Children[0].StateMessage = "完成";
        group.Children[1].Progress = 0.62;
        group.Children[1].StateMessage = "62%";
        group.Children[2].Progress = 0.18;
        group.Children[2].StateMessage = "18%";
        group.Children[3].StateMessage = "等待中";
        group.Progress = 0.45;
        group.State = TaskState.Running;
        group.StateMessage = "进行中";
        TaskCenter.Register(group);
    }
}
