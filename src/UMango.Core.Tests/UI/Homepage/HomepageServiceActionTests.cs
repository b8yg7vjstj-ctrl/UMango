using System.IO;
using UMango.Core.UI.Homepage;
using Xunit;

namespace UMango.Core.Tests.UI.Homepage;

/// <summary>
/// 「清单 → 动作白名单」这条链路的端到端行为。
///
/// 这一组测的是最容易出错的地方：白名单必须**在解析阶段**就把未注册的动作丢掉，
/// 而不是等到执行时才发现 —— 也就是说，攻击者不能靠清单引入新动作。
/// </summary>
public class HomepageServiceActionTests
{
    private static HomepageActionRegistry Registry() =>
        new HomepageActionRegistry()
            .Register(new HomepageActionDescriptor("open-downloads", "下载页"))
            .Register(new HomepageActionDescriptor("danger", "危险动作", requiresConfirmation: true));

    [Fact]
    public void 注册表里查不到的动作返回_null()
    {
        var registry = Registry();
        Assert.Null(registry.Find("nope"));
        Assert.False(registry.IsRegistered("nope"));
    }

    [Fact]
    public void 动作名查找是忽略大小写的()
    {
        var registry = Registry();
        Assert.True(registry.IsRegistered("OPEN-DOWNLOADS"));
        Assert.True(registry.IsRegistered("Open-Downloads"));
    }

    [Fact]
    public void 需要确认的标记被保留()
    {
        var registry = Registry();
        Assert.True(registry.Find("danger")!.RequiresConfirmation);
        Assert.False(registry.Find("open-downloads")!.RequiresConfirmation);
    }

    [Fact]
    public void 空名字不能注册()
    {
        var registry = new HomepageActionRegistry();
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new HomepageActionDescriptor("", "空")));
    }

    [Fact]
    public void 清单里引用已注册动作的卡片会被保留()
    {
        var result = HomepageManifestParser.Parse(
            """{"cards":[{"title":"A","action":"open-downloads"}]}""",
            Registry().IsRegistered);

        Assert.Null(result.Error);
        Assert.Single(result.Manifest!.Cards);
        Assert.Equal(0, result.DroppedCards);
    }

    [Fact]
    public void 清单里引用未注册动作的卡片被丢弃()
    {
        var result = HomepageManifestParser.Parse(
            """{"cards":[{"title":"A","action":"run-anything"}]}""",
            Registry().IsRegistered);

        Assert.Equal(1, result.DroppedCards);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 服务加载时用的是同一个注册表()
    {
        // 回归测试：曾经出现过 Service 拿空注册表、Host 拿另一个注册表的情况，
        // 结果所有带动作的卡片都被丢掉（功能看着"正常"，只是卡片永远不出现）。
        var dir = Path.Combine(Path.GetTempPath(), "umango-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "homepage.json");
            File.WriteAllText(path, """{"cards":[{"title":"A","action":"open-downloads"}]}""");

            var actions = Registry();
            var service = new HomepageService(path, actions);

            var result = service.Load();

            Assert.Single(result.Manifest!.Cards);
            Assert.Equal(0, result.DroppedCards);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 清单文件不存在时返回空清单而不是错误()
    {
        var path = Path.Combine(Path.GetTempPath(), "umango-home-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var service = new HomepageService(path, Registry());

        var result = service.Load();

        Assert.Null(result.Error);
        Assert.Empty(result.Manifest!.Cards);
    }
}
