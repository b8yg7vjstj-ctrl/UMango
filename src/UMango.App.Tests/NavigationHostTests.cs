using UMango.App.Services;
using UMango.Shell.ViewModels;
using UMango.Core.UI.Navigation;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 导航宿主测试：Core 的导航栈 → 外壳状态（副标题 / 返回按钮可用性 / 是否次级页）。
///
/// 不涉及 WPF 类型（NavigationHost 只用 CommunityToolkit.Mvvm 的 ObservableObject），
/// 因此可以在 macOS 上跑。
/// </summary>
public class NavigationHostTests
{
    private static (NavigationHost Host, NavigationService Nav) NewHost()
    {
        var nav = new NavigationService("home", "主页");
        return (new NavigationHost(nav), nav);
    }

    [Fact]
    public void 初始状态不是次级页面()
    {
        var (host, _) = NewHost();

        Assert.False(host.IsSecondary);
        Assert.False(host.CanGoBack);
        Assert.Null(host.Subtitle);
    }

    [Fact]
    public void 压栈后副标题变成页面标题()
    {
        var (host, nav) = NewHost();

        nav.Push("detail", "详情");

        Assert.Equal("详情", host.Subtitle);
    }

    [Fact]
    public void 压栈后成为次级页面且可返回()
    {
        var (host, nav) = NewHost();

        nav.Push("detail", "详情");

        Assert.True(host.IsSecondary);
        Assert.True(host.CanGoBack);
    }

    [Fact]
    public void 返回后副标题回到上一层()
    {
        var (host, nav) = NewHost();
        nav.Push("detail", "详情");
        nav.Push("more", "更多");

        nav.Back();

        Assert.Equal("详情", host.Subtitle);
    }

    [Fact]
    public void 返回到根后不再是次级页面()
    {
        var (host, nav) = NewHost();
        nav.Push("detail", "详情");

        nav.Back();

        Assert.False(host.IsSecondary);
        Assert.False(host.CanGoBack);
    }

    [Fact]
    public void 返回命令在有栈时真的回退()
    {
        var (host, nav) = NewHost();
        nav.Push("detail", "详情");

        host.GoBackCommand.Execute(null);

        Assert.False(host.CanGoBack);
        Assert.Equal("home", nav.Current.Page.Value);
    }

    [Fact]
    public void 返回命令在栈空时不动()
    {
        var (host, nav) = NewHost();

        host.GoBackCommand.Execute(null);

        Assert.Equal("home", nav.Current.Page.Value);
    }

    [Fact]
    public void 回主页面后副标题变回主页面标题()
    {
        var (host, nav) = NewHost();
        nav.Push("detail", "详情");

        nav.NavigateRoot(new NavigationEntry(new NavigationPageId("home"), "主页"));

        Assert.Equal("主页", host.Subtitle);
        Assert.False(host.IsSecondary);
    }
}
