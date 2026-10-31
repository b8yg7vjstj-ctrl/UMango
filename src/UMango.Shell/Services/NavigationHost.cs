using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.UI.Navigation;

namespace UMango.Shell.Services;

/// <summary>
/// 导航宿主：把 Core 的 <see cref="NavigationService"/> 接到外壳上（两个壳共用）。
///
/// 职责边界：
///   · <see cref="NavigationService"/>（Core，纯逻辑）负责「栈」本身；
///   · 本类负责「栈状态 → 界面」：副标题标题、返回按钮的可见性与可用性。
///
/// 页面内容由 MainWindow 按导航键自己裁决（外壳不替页面做内容切换），
/// 这里只回答一个问题：**当前是不是在某个次级页面上、能不能返回**。
/// </summary>
public partial class NavigationHost : ObservableObject
{
    private readonly NavigationService _navigation;

    public NavigationHost(NavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _navigation = navigation;
        _navigation.Navigated += OnNavigated;
    }

    /// <summary>底层导航服务（供需要精细控制的地方使用）。</summary>
    public NavigationService Navigation => _navigation;

    /// <summary>当前页标题（次级页面时显示在副标题栏）。</summary>
    [ObservableProperty]
    private string? _subtitle;

    /// <summary>是否可以返回（栈里还有上一层）。</summary>
    [ObservableProperty]
    private bool _canGoBack;

    /// <summary>当前是否处于次级页面（决定副标题栏整体显隐）。</summary>
    [ObservableProperty]
    private bool _isSecondary;

    /// <summary>返回上一层。</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (!Navigation.CanGoBack) return;

        Navigation.Back();
    }

    private void OnNavigated(object? sender, NavigationSnapshot snapshot)
    {
        Subtitle = snapshot.Title;
        CanGoBack = snapshot.CanGoBack;
        IsSecondary = snapshot.Current.IsSecondary;
    }
}
