using UMango.Shell.Icons;
using Xunit;

namespace UMango.Shell.Tests;

/// <summary>
/// 图标包资源的回归测试。
///
/// <para>
/// 图标从 <c>src/UMango.App/Assets</c> 移到了 <c>src/UMango.Shell/Assets</c>（两个壳共用一份），
/// 加载方式也从 WPF 的 pack URI 换成「读本程序集的内嵌资源」。
/// 这类"路径/资源名拼错"的故障不会在编译期暴露，界面上只表现为**图标静默消失**，
/// 所以用测试把资源名与解析结果锁住。
/// </para>
/// </summary>
public class IconResourceTests
{
    /// <summary>左栏导航用到的全部图标（与 WPF 侧 NavIconMap 的取值一一对应）。</summary>
    private static readonly string[] NavIcons =
    [
        "home", "list-checks", "database", "list", "star", "loader", "circle-check",
        "palette", "bug", "scale", "download", "scroll-text", "activity",
        "package-search", "message-square", "music", "info",
    ];

    [Fact]
    public void 图标资源名符合约定前缀()
    {
        var resources = SvgIconLoader.ListResources();
        Assert.NotEmpty(resources);
        Assert.All(resources, name =>
            Assert.StartsWith("UMango.Shell.Assets.IconPacks.", name, StringComparison.Ordinal));
    }

    [Fact]
    public void 所有导航图标都能加载并解析出图元()
    {
        foreach (var name in NavIcons)
        {
            var model = SvgIconLoader.Load("lucide/" + name);

            Assert.True(model is not null, $"取不到图标 lucide/{name}");
            Assert.NotEmpty(model!.Shapes);
            Assert.True(model.Width > 0 && model.Height > 0, $"{name} 的 viewBox 尺寸无效");
        }
    }

    [Fact]
    public void 每个左栏导航项都有可加载的图标()
    {
        var shell = new UMango.Shell.ViewModels.ShellViewModel();

        foreach (var tab in Enum.GetValues<UMango.Shell.ViewModels.AppTab>())
        {
            shell.CurrentTab = tab;

            Assert.NotEmpty(shell.LeftMenu);
            foreach (var item in shell.LeftMenu)
            {
                Assert.False(string.IsNullOrEmpty(item.Icon), $"导航项 {item.Key} 没有配图标");
                Assert.True(SvgIconLoader.Load(item.Icon) is not null, $"导航项 {item.Key} 的图标取不到");
            }
        }
    }

    [Fact]
    public void 不存在的图标返回空而不是抛异常()
    {
        Assert.Null(SvgIconLoader.Load("lucide/__not_exist__"));
        Assert.Null(SvgIconLoader.Load(""));
        Assert.Null(SvgIconLoader.Load(null));
    }
}
