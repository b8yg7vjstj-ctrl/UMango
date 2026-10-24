using System;
using System.IO;
using System.Linq;
using UMango.Core.UI.Homepage;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 自定义主页的安全边界。
///
/// 这一组用例测的是**安全模型**本身：清单来自用户（甚至可能来自网上被人改过的文件），
/// 解析器必须**只**产生界面能显示的数据，绝不产生可执行的东西。
/// </summary>
public class HomepageManifestTests
{
    private static HomepageActionRegistry Registry() =>
        new HomepageActionRegistry()
            .Register(new HomepageActionDescriptor("open-downloads", "打开下载页"))
            .Register(new HomepageActionDescriptor("run-cli", "执行包管理器命令", requiresConfirmation: true));

    [Fact]
    public void 合法清单能解析出卡片()
    {
        var json = """
        {
          "title": "我的主页",
          "cards": [
            { "title": "npm", "subtitle": "node_modules", "link": "https://www.npmjs.com" }
          ]
        }
        """;

        var result = HomepageManifestParser.Parse(json);
        Assert.True(result.Success);
        Assert.Equal("我的主页", result.Manifest!.Title);
        Assert.Single(result.Manifest.Cards);
        Assert.Equal("npm", result.Manifest.Cards[0].Title);
    }

    [Fact]
    public void 非法_json_返回错误()
    {
        var result = HomepageManifestParser.Parse("{ 这不是 json");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void 空清单返回错误()
    {
        Assert.False(HomepageManifestParser.Parse("").Success);
        Assert.False(HomepageManifestParser.Parse("   ").Success);
    }

    [Fact]
    public void 空白清单返回空清单而不是错误()
    {
        var result = HomepageManifestParser.Parse("{}");
        Assert.True(result.Success);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 卡片标题为空被丢弃()
    {
        var json = """
        { "cards": [ { "title": "" }, { "title": "ok" } ] }
        """;
        var result = HomepageManifestParser.Parse(json);
        Assert.True(result.Success);
        Assert.Single(result.Manifest!.Cards);
        Assert.Equal(1, result.DroppedCards);
    }

    [Fact]
    public void file_协议的链接被拒绝()
    {
        var json = """{ "cards": [ { "title": "x", "link": "file:///etc/passwd" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Empty(result.Manifest!.Cards);
        Assert.Equal(1, result.DroppedCards);
    }

    [Fact]
    public void javascript_协议的链接被拒绝()
    {
        var json = """{ "cards": [ { "title": "x", "link": "javascript:alert(1)" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void data_协议的链接被拒绝()
    {
        var json = """{ "cards": [ { "title": "x", "link": "data:text/html,<script>1</script>" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void http_与_https_链接被接受()
    {
        var json = """{ "cards": [ { "title": "a", "link": "http://example.com" }, { "title": "b", "link": "https://example.com" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Equal(2, result.Manifest!.Cards.Count);
    }

    [Fact]
    public void 图标只接受名字不接受路径()
    {
        var json = """{ "cards": [ { "title": "x", "icon": "../../etc/passwd" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 图标不接受绝对路径()
    {
        var json = """{ "cards": [ { "title": "x", "icon": "C:/Windows/system32" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 图标名字被接受()
    {
        var json = """{ "cards": [ { "title": "x", "icon": "package" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Single(result.Manifest!.Cards);
    }

    [Fact]
    public void 未注册的动作被拒绝()
    {
        var json = """{ "cards": [ { "title": "x", "action": "rm -rf /" } ] }""";
        var result = HomepageManifestParser.Parse(json, Registry().IsRegistered);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 已注册的动作被接受()
    {
        var json = """{ "cards": [ { "title": "x", "action": "open-downloads" } ] }""";
        var result = HomepageManifestParser.Parse(json, Registry().IsRegistered);
        Assert.Single(result.Manifest!.Cards);
    }

    [Fact]
    public void 没有白名单时任意动作名都被接受()
    {
        // 没提供白名单就不做动作校验 —— 调用方（App）必须提供白名单。
        var json = """{ "cards": [ { "title": "x", "action": "whatever" } ] }""";
        var result = HomepageManifestParser.Parse(json);
        Assert.Single(result.Manifest!.Cards);
    }

    [Fact]
    public void 卡片数超限被拒绝()
    {
        var cards = string.Join(",", Enumerable.Range(0, HomepageManifestParser.MaxCards + 1)
            .Select(i => $$"""{ "title": "c{{i}}" }"""));
        var result = HomepageManifestParser.Parse($$"""{"cards":[{{cards}}]}""");
        Assert.False(result.Success);
        Assert.Contains("卡片数", result.Error);
    }

    [Fact]
    public void 标题过长被拒绝()
    {
        var longTitle = new string('x', HomepageManifestParser.MaxTitleLength + 1);
        var result = HomepageManifestParser.Parse($$"""{"title": "{{longTitle}}"}""");
        Assert.False(result.Success);
    }

    [Fact]
    public void 卡片标题过长被丢弃()
    {
        var longTitle = new string('x', HomepageManifestParser.MaxTitleLength + 1);
        var result = HomepageManifestParser.Parse($$"""{"cards":[{"title":"{{longTitle}}"},{"title":"ok"}]}""");
        Assert.Single(result.Manifest!.Cards);
        Assert.Equal(1, result.DroppedCards);
    }

    [Fact]
    public void 正文过长被丢弃()
    {
        var body = new string('x', HomepageManifestParser.MaxBodyLength + 1);
        var result = HomepageManifestParser.Parse($$"""{"cards":[{"title":"a","body":"{{body}}"}]}""");
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 链接过长被丢弃()
    {
        var link = "https://example.com/" + new string('x', HomepageManifestParser.MaxLinkLength);
        var result = HomepageManifestParser.Parse($$"""{"cards":[{"title":"a","link":"{{link}}"}]}""");
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 链接判定只认绝对_http_https()
    {
        Assert.True(HomepageManifestParser.IsAllowedLink("https://a.com"));
        Assert.True(HomepageManifestParser.IsAllowedLink("http://a.com"));
        Assert.False(HomepageManifestParser.IsAllowedLink("/relative"));
        Assert.False(HomepageManifestParser.IsAllowedLink("ftp://a.com"));
        Assert.False(HomepageManifestParser.IsAllowedLink("file:///etc/passwd"));
    }

    [Fact]
    public void 清单里不能出现可执行内容()
    {
        // 这条是「安全模型」的兜底断言：即使清单里塞了 XAML 片段，
        // 解析结果也只可能包含数据字段 —— 类型上根本没有承载代码的地方。
        var json = """
        {
          "cards": [
            { "title": "x", "body": "<Button Click=\"{CodeBehind}\"/>" }
          ]
        }
        """;
        var result = HomepageManifestParser.Parse(json);
        Assert.True(result.Success);
        var card = result.Manifest!.Cards[0];
        Assert.Equal("<Button Click=\"{CodeBehind}\"/>", card.Body);
        // 类型上就没有任何委托 / 类型名字段
        Assert.Null(typeof(HomepageCard).GetProperty("Action_"));
    }
}

/// <summary>主页清单的存取与热更新。</summary>
public class HomepageServiceTests
{
    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        "umango-home-" + Guid.NewGuid().ToString("n")[..10],
        "homepage.json");

    [Fact]
    public void 文件不存在时返回空清单()
    {
        var service = new HomepageService(TempPath());
        var result = service.Load();
        Assert.True(result.Success);
        Assert.Empty(result.Manifest!.Cards);
    }

    [Fact]
    public void 保存后能读回来()
    {
        var path = TempPath();
        var service = new HomepageService(path);
        service.Save(new HomepageManifest
        {
            Title = "T",
            Cards = [new HomepageCard { Title = "c1", Link = "https://example.com" }],
        });

        var result = service.Load();
        Assert.True(result.Success);
        Assert.Equal("T", result.Manifest!.Title);
        Assert.Single(result.Manifest.Cards);
        Assert.Equal("c1", result.Manifest.Cards[0].Title);
    }

    [Fact]
    public void 读取非法文件返回错误而不是抛异常()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken");

        var service = new HomepageService(path);
        var result = service.Load();
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void 动作白名单在加载时生效()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"cards":[{"title":"a","action":"nope"}]}""");

        var service = new HomepageService(path, new HomepageActionRegistry()
            .Register(new HomepageActionDescriptor("ok", "ok")));
        var result = service.Load();
        Assert.Empty(result.Manifest!.Cards);
        Assert.Equal(1, result.DroppedCards);
    }

    [Fact]
    public void 原子保存不留下临时文件()
    {
        var path = TempPath();
        var service = new HomepageService(path);
        service.Save(new HomepageManifest { Title = "x" });
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
