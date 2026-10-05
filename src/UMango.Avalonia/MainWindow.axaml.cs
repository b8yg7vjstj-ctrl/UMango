using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UMango.Core.Animation;
using UMango.Core.Animation.Clock;
using UMango.Avalonia.Views;

namespace UMango.Avalonia;

public partial class MainWindow : Window
{
    private readonly HomeView _home = new();

    /// <summary>
    /// 共享动画服务：纯托管时钟 + 帧回调编组到 UI 线程。
    /// 与 WPF 版同构（那边用 WpfClock），引擎与数值完全同一套。
    /// </summary>
    private static readonly AnimationService Service =
        new(new StopwatchClock(60), a => Dispatcher.UIThread.Post(a));

    public MainWindow()
    {
        InitializeComponent();
        ShowPage(_home, animate: false);
        TabHome.Classes.Add("active");
    }

    /// <summary>
    /// 切页：换内容 + 用 Core 引擎播 PCL 原版入场动效。
    /// 数值来自 PageMotionProfile.Pcl（淡入 100ms、两段落体 250/350ms BackOut），
    /// 与 WPF 版完全一致 —— 两个壳，一套动效。
    /// </summary>
    private void ShowPage(Control page, bool animate = true)
    {
        PageHost.Content = page;

        if (!animate) return;
        var settings = new AnimationSettings();   // PCL 预设为默认
        var (x, y, o) = AvaloniaAnimatable.FromElement(page);

        // 起始态：透明 + 上方 16px（PCL 原版的入场起点）
        o.SetValue(0.0);
        y.SetValue(-16.0);

        var binding = AnimationPlannerBinding.CreatePageEnter(
            PageMotionProfile.Pcl, x, y, o, settings, fps: 60);
        Service.RunFireAndForget(binding);
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        // 第一版只实现了主页；其余页后续轮次补齐（避免一次性铺开导致每页都残）
        if (!ReferenceEquals(sender, TabHome))
        {
            ShowPage(_home);
            return;
        }
        ShowPage(_home);
    }
}
