using UMango.App.Services;
using UMango.App.ViewModels;
using UMango.Core.UI.Navigation;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 左栏 ↔ 导航栈的映射规则测试。
///
/// 这套规则是"点返回能不能正确回到上一层"的要害：
/// 只还原选项卡或只还原左栏高亮都会让界面自相矛盾。
/// </summary>
public class NavStackMappingTests
{
    private static NavItem Primary() => new("overview", "概览");

    private static NavItem Secondary() => new("logs", "日志", IsSecondary: true);

    // ---------- 页面标识 ----------

    [Fact]
    public void 页面标识由选项卡和菜单键组成()
    {
        Assert.Equal("Tools/logs", NavStackMapping.PageId(AppTab.Tools, "logs"));
    }

    [Theory]
    [InlineData("Tools/logs", AppTab.Tools, "logs")]
    [InlineData("Home/overview", AppTab.Home, "overview")]
    [InlineData("Settings/debug", AppTab.Settings, "debug")]
    public void 页面标识可以拆回去(string id, AppTab tab, string key)
    {
        Assert.True(NavStackMapping.TryParse(id, out var parsedTab, out var parsedKey));
        Assert.Equal(tab, parsedTab);
        Assert.Equal(key, parsedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Tools")]
    [InlineData("Tools/")]
    [InlineData("/logs")]
    [InlineData("NotATab/logs")]
    public void 非法页面标识会被拒绝(string? id)
    {
        Assert.False(NavStackMapping.TryParse(id, out _, out _));
    }

    [Fact]
    public void 拆出来的键以第一个斜杠为界()
    {
        // 键里若含斜杠，只按第一个拆，剩下的整体作为键
        Assert.True(NavStackMapping.TryParse("Tools/a/b", out var tab, out var key));
        Assert.Equal(AppTab.Tools, tab);
        Assert.Equal("a/b", key);
    }

    // ---------- 解析成导航条目 ----------

    [Fact]
    public void 次级项解析成次级条目()
    {
        var entry = NavStackMapping.Resolve(Secondary(), AppTab.Tools, "Tools/downloader");

        Assert.NotNull(entry);
        Assert.True(entry!.IsSecondary);
        Assert.Equal("Tools/logs", entry.Page.Value);
        Assert.Equal("日志", entry.Title);
        Assert.Equal("logs", entry.Parameter);
    }

    [Fact]
    public void 主项解析成非次级条目()
    {
        var entry = NavStackMapping.Resolve(Primary(), AppTab.Home, "Home/records");

        Assert.NotNull(entry);
        Assert.False(entry!.IsSecondary);
        Assert.Equal("Home/overview", entry.Page.Value);
    }

    [Fact]
    public void 空菜单项解析成null()
    {
        Assert.Null(NavStackMapping.Resolve(null, AppTab.Home, "Home/overview"));
    }

    [Fact]
    public void 重复点选同一个次级项不再压一层()
    {
        Assert.Null(NavStackMapping.Resolve(Secondary(), AppTab.Tools, "Tools/logs"));
    }

    [Fact]
    public void 换到另一个次级项会压新的()
    {
        Assert.NotNull(NavStackMapping.Resolve(Secondary(), AppTab.Tools, "Tools/downloader"));
    }

    [Fact]
    public void 主项即使已经在当前页也会产生条目()
    {
        // 主项要清栈，所以必须总是产生条目（否则点了主项却清不掉栈）。
        Assert.NotNull(NavStackMapping.Resolve(Primary(), AppTab.Home, "Home/overview"));
    }

    // ---------- 与真实导航栈配合 ----------

    [Fact]
    public void 完整流程_进入次级页再返回()
    {
        var nav = new NavigationService("Home/overview", "概览");
        var shell = new ShellViewModel();

        // 模拟：左栏切到「工具」选项卡的「日志」
        shell.CurrentTab = AppTab.Tools;
        var entry = NavStackMapping.Resolve(
            shell.LeftMenu.First(i => i.Key == "logs"), shell.CurrentTab, nav.Current.Page.Value);
        Assert.NotNull(entry);
        nav.Push(entry!);

        Assert.True(nav.CanGoBack);
        Assert.Equal("Tools/logs", nav.Current.Page.Value);

        // 返回
        Assert.True(nav.Back());
        Assert.Equal("Home/overview", nav.Current.Page.Value);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void 完整流程_连点两次不会压出两层()
    {
        var nav = new NavigationService("Tools/downloader", "下载器");
        var item = new NavItem("logs", "日志", IsSecondary: true);

        var first = NavStackMapping.Resolve(item, AppTab.Tools, nav.Current.Page.Value);
        nav.Push(first!);

        var second = NavStackMapping.Resolve(item, AppTab.Tools, nav.Current.Page.Value);
        Assert.Null(second);

        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void 返回后能正确拆出上一层的选项卡与菜单键()
    {
        var nav = new NavigationService("Home/overview", "概览");
        nav.Push(NavStackMapping.Resolve(
            new NavItem("logs", "日志", IsSecondary: true), AppTab.Tools, "Home/overview")!);
        nav.Push(NavStackMapping.Resolve(
            new NavItem("about", "关于", IsSecondary: true), AppTab.Tools, "Tools/logs")!);

        nav.Back();

        Assert.True(NavStackMapping.TryParse(nav.Current.Page.Value, out var tab, out var key));
        Assert.Equal(AppTab.Tools, tab);
        Assert.Equal("logs", key);
    }
}
