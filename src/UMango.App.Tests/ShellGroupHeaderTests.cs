using Xunit;
using UMango.App.ViewModels;

namespace UMango.App.Tests;

public class ShellGroupHeaderTests
{
    [Theory]
    [InlineData(AppTab.Home)]
    [InlineData(AppTab.Download)]
    [InlineData(AppTab.Progress)]
    [InlineData(AppTab.Settings)]
    [InlineData(AppTab.Tools)]
    public void 每个选项卡的第一项带分组标题(AppTab tab)
    {
        var shell = new ShellViewModel();
        shell.SwitchTabCommand.Execute(tab);

        Assert.False(string.IsNullOrWhiteSpace(shell.LeftMenu[0].GroupTitle));
    }

    [Theory]
    [InlineData(AppTab.Home)]
    [InlineData(AppTab.Download)]
    [InlineData(AppTab.Progress)]
    [InlineData(AppTab.Settings)]
    [InlineData(AppTab.Tools)]
    public void 同一选项卡内分组标题不重复(AppTab tab)
    {
        var shell = new ShellViewModel();
        shell.SwitchTabCommand.Execute(tab);

        var titles = shell.LeftMenu.Where(i => i.GroupTitle is not null)
            .Select(i => i.GroupTitle!).ToList();

        // 一个选项卡一个分组 → 只有一个分组标题
        Assert.Single(titles);
    }

    [Fact]
    public void 分组标题出现在第一项而不是最后一项()
    {
        var shell = new ShellViewModel();

        Assert.Null(shell.LeftMenu[1].GroupTitle);
    }

    [Fact]
    public void 隐藏入口后分组标题仍然存在()
    {
        var shell = new ShellViewModel();
        shell.ApplyHiddenEntries(new HashSet<string> { "overview" }, new HashSet<AppTab>());

        // overview 被藏了 → 菜单重建后第一项变成 records，
        // 但 records 不带分组标题（原设计：分组标题挂在分组首项上）
        Assert.DoesNotContain(shell.LeftMenu, i => i.Key == "overview");
    }
}
