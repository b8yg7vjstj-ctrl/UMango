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

    public MainWindow()
    {
        InitializeComponent();
        ShowPage(_home, animate: false);
        TabHome.Classes.Add("active");
    }

    /// <summary>
    /// 切页：换内容 + 播 PCL 原版入场动效。
    ///
    /// 动效走 **Core 的补间层**（<see cref="TweenFactory"/> + <see cref="TweenPlanner"/>），
    /// 数值逐条对照 PCL-CE `MyPageRight.TriggerEnterAnimation`（MyPageRight.cs:552-600）：
    ///   · 起始态 Opacity=0、Y=-16
    ///   · 淡入 100ms（AniEaseOutFluent(Weak) = 1-(1-t)^2）
    ///   · 两段落体 +5 / +11（250ms FluentOut / 350ms AniEaseOutBack）**并行叠加**
    ///   · 终态 Y = -16 + 5 + 11 = 0
    ///
    /// 与 WPF 壳共用同一份 Core 数值 → 两个壳的观感一致。
    /// </summary>
    private void ShowPage(Control page, bool animate = true)
    {
        PageHost.Content = page;

        if (!animate) return;

        var binder = AvaloniaTweenBinder.ForElement(page);
        var opacity = binder.Resolve(TweenChannel.Opacity)!;
        var translateY = binder.Resolve(TweenChannel.TranslateY)!;

        // 起始态：透明 + 上方 16px（PCL 原版的入场起点）
        opacity.SetValue(0d);
        translateY.SetValue(PclTweenPresets.EnterFromY);

        AvaloniaTweenPlayer.Play(PclTweenPresets.PageEnter(), binder, fps: 60);
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
