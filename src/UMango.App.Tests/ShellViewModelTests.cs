using UMango.App.ViewModels;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 外壳 ViewModel 测试 —— 这一层此前**零覆盖**。
///
/// 它驱动顶部选项卡与左栏菜单，是"页面能不能被打开"的总开关：
/// 菜单项写错、切换时没重建菜单、选中项没复位，都会表现为"点了没反应或右区与高亮不一致"，
/// 而这类问题在 XAML 静态审计里查不出来（绑定与资源都是对的，逻辑错了）。
/// </summary>
[Collection("静态状态")]
public class ShellViewModelTests
{
    [Fact]
    public void 构造时就建好左栏菜单_否则首帧左栏是空的()
    {
        var shell = new ShellViewModel();
        Assert.NotEmpty(shell.LeftMenu);
    }

    [Fact]
    public void 默认停在主页()
    {
        var shell = new ShellViewModel();
        Assert.Equal(AppTab.Home, shell.CurrentTab);
        Assert.Contains(shell.LeftMenu, i => i.Key == "overview");
    }

    [Fact]
    public void 切换选项卡会重建菜单并把选中项复位到第一项()
    {
        var shell = new ShellViewModel();
        shell.SelectNavCommand.Execute(shell.LeftMenu.Last());   // 先选到非第一项
        Assert.NotEqual("overview", shell.CurrentNavKey);

        shell.CurrentTab = AppTab.Progress;

        Assert.Equal("running", shell.CurrentNavKey);            // 复位了
        Assert.Contains(shell.LeftMenu, i => i.Key == "running");
        Assert.DoesNotContain(shell.LeftMenu, i => i.Key == "overview");
    }

    [Theory]
    [InlineData(AppTab.Home, "overview")]
    [InlineData(AppTab.Download, "sources")]
    [InlineData(AppTab.Progress, "running")]
    [InlineData(AppTab.Settings, "theme")]
    [InlineData(AppTab.Tools, "downloader")]
    public void 每个选项卡的第一项菜单符合预期(AppTab tab, string expectedFirstKey)
    {
        var shell = new ShellViewModel { CurrentTab = tab };
        Assert.Equal(expectedFirstKey, shell.LeftMenu[0].Key);
        Assert.Equal(expectedFirstKey, shell.CurrentNavKey);
    }

    [Fact]
    public void 工具页菜单包含下载器入口()
    {
        // 下载器放在工具页下（对应 PCL-CE 的百宝箱位置），
        // 而 MainWindow 里靠 CurrentNavKey == "downloader" 来决定换不换页面 ——
        // 这个 key 一旦被改名，下载器就再也打不开了。
        var shell = new ShellViewModel { CurrentTab = AppTab.Tools };
        Assert.Contains(shell.LeftMenu, i => i.Key == "downloader");
    }

    [Fact]
    public void 切换菜单项会触发NavChanged事件()
    {
        var shell = new ShellViewModel();
        var seen = new List<string>();
        shell.NavChanged += k => seen.Add(k);

        var target = shell.LeftMenu[1];
        shell.SelectNavCommand.Execute(target);

        Assert.Contains(target.Key, seen);
    }

    [Fact]
    public void SelectNav支持直接传键名()
    {
        var shell = new ShellViewModel { CurrentTab = AppTab.Settings };
        shell.SelectNavCommand.Execute("debug");
        Assert.Equal("debug", shell.CurrentNavKey);
    }

    [Fact]
    public void SelectNav对非法参数安全()
    {
        var shell = new ShellViewModel();
        var before = shell.CurrentNavKey;

        shell.SelectNavCommand.Execute(null);
        shell.SelectNavCommand.Execute(42);
        shell.SelectNavCommand.Execute("   ");

        Assert.Equal(before, shell.CurrentNavKey);   // 不应被改坏
    }

    [Fact]
    public void 左栏菜单标题都是中文且非空()
    {
        // 需求要求界面文案中文；空标题会在左栏留出一条看不见的空白项
        var shell = new ShellViewModel();
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            shell.CurrentTab = tab;
            Assert.All(shell.LeftMenu, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Title), $"{tab} 有菜单项标题为空");
                Assert.False(string.IsNullOrWhiteSpace(item.Key), $"{tab} 有菜单项 Key 为空");
            });
        }
    }

    [Fact]
    public void 各选项卡的菜单键不重复()
    {
        var shell = new ShellViewModel();
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            shell.CurrentTab = tab;
            var keys = shell.LeftMenu.Select(i => i.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }

    [Fact]
    public void SwitchTab命令与直接赋值等价()
    {
        var shell = new ShellViewModel();
        shell.SwitchTabCommand.Execute(AppTab.Tools);
        Assert.Equal(AppTab.Tools, shell.CurrentTab);
    }

    [Fact]
    public void 五个选项卡都有对应的左栏菜单()
    {
        var shell = new ShellViewModel();
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            shell.CurrentTab = tab;
            Assert.NotEmpty(shell.LeftMenu);
        }
        Assert.Equal(5, Enum.GetValues<AppTab>().Length);
    }
}

/// <summary>功能隐藏：外壳菜单的过滤。</summary>
public sealed class ShellHiddenEntriesTests
{
    [Fact]
    public void 藏掉日志入口_工具页菜单里没有它()
    {
        var shell = new ShellViewModel();
        shell.ApplyHiddenEntries(new HashSet<string> { "logs" }, new HashSet<AppTab>());

        shell.CurrentTab = AppTab.Tools;

        Assert.DoesNotContain(shell.LeftMenu, i => i.Key == "logs");
        Assert.Contains(shell.LeftMenu, i => i.Key == "feedback");
    }

    [Fact]
    public void 藏掉下载器入口_工具页菜单里没有它()
    {
        var shell = new ShellViewModel();
        shell.ApplyHiddenEntries(new HashSet<string> { "downloader" }, new HashSet<AppTab>());

        shell.CurrentTab = AppTab.Tools;

        Assert.DoesNotContain(shell.LeftMenu, i => i.Key == "downloader");
    }

    [Fact]
    public void 不藏任何东西_菜单不变()
    {
        var shell = new ShellViewModel();
        var before = shell.LeftMenu.Count;

        shell.ApplyHiddenEntries(new HashSet<string>(), new HashSet<AppTab>());

        Assert.Equal(before, shell.LeftMenu.Count);
    }

    [Fact]
    public void 藏掉音乐入口_其它入口都还在()
    {
        var shell = new ShellViewModel();
        shell.ApplyHiddenEntries(new HashSet<string> { "music" }, new HashSet<AppTab>());

        shell.CurrentTab = AppTab.Tools;

        Assert.DoesNotContain(shell.LeftMenu, i => i.Key == "music");
        Assert.Contains(shell.LeftMenu, i => i.Key == "about");
        Assert.Contains(shell.LeftMenu, i => i.Key == "licenses");
    }

    [Fact]
    public void 隐藏工具入口不影响主页菜单()
    {
        var shell = new ShellViewModel();
        shell.ApplyHiddenEntries(new HashSet<string> { "logs" }, new HashSet<AppTab>());

        // 主页菜单里本来就没有 logs，条目数不变
        Assert.Contains(shell.LeftMenu, i => i.Key == "overview");
    }
}
