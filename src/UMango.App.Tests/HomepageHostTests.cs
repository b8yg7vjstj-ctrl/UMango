using UMango.App.Services;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 主页动作的执行路径。
///
/// 这些用例守的是「**清单里的动作名必须先注册才能执行**」这条边界 ——
/// 白名单判定在 Core 的解析阶段，这里是第二道防线：
/// 即使有人绕过解析直接调 InvokeAction，未注册的名字也必须什么也不做。
/// </summary>
public class HomepageHostTests
{
    [Fact]
    public void 未注册的动作不会被执行()
    {
        var host = new HomepageHost();
        var called = false;
        host.NavigationRequested += _ => called = true;

        var ok = host.InvokeAction("run-anything", null);

        Assert.False(ok);
        Assert.False(called);
    }

    [Fact]
    public void 已注册的动作会触发导航()
    {
        var host = new HomepageHost();
        string? target = null;
        host.NavigationRequested += t => target = t;

        var ok = host.InvokeAction("open-settings", null);

        Assert.True(ok);
        Assert.Equal("settings", target);
    }

    [Fact]
    public void 动作名匹配忽略大小写()
    {
        var host = new HomepageHost();
        string? target = null;
        host.NavigationRequested += t => target = t;

        Assert.True(host.InvokeAction("OPEN-SETTINGS", null));
        Assert.Equal("settings", target);
    }

    [Fact]
    public void 刷新动作触发刷新请求而不是导航()
    {
        var host = new HomepageHost();
        var refreshed = false;
        var navigated = false;
        host.RefreshRequested += () => refreshed = true;
        host.NavigationRequested += _ => navigated = true;

        Assert.True(host.InvokeAction("refresh-sources", null));
        Assert.True(refreshed);
        Assert.False(navigated);
    }

    [Fact]
    public void 清单路径是绝对路径()
    {
        var host = new HomepageHost();
        Assert.True(Path.IsPathRooted(host.ManifestPath));
        Assert.Equal("homepage.json", Path.GetFileName(host.ManifestPath));
    }

    [Fact]
    public void 清单文件不存在时加载出空清单而不是报错()
    {
        var host = new HomepageHost();
        var result = host.Load();

        Assert.Null(result.Error);
        Assert.Empty(result.Manifest!.Cards);
    }
}

/// <summary>
/// 清单 → 卡片 的 App 层端到端。
///
/// 这一组专门咬住一个曾经真实发生过的 bug：
/// Host 有一份动作注册表、Service 拿的是**另一份（空的）**注册表，
/// 结果清单里所有带动作的卡片在解析阶段就被丢掉 ——
/// 功能看起来"正常"（不报错、不崩），只是卡片永远不出现。
/// </summary>
public class HomepageManifestRoundTripTests
{
    private static string TempManifest() => Path.Combine(
        Path.GetTempPath(), "umango-home-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void 清单里的已注册动作卡片能被加载出来()
    {
        var path = TempManifest();
        try
        {
            var host = new HomepageHost(path);
            host.Service.Save(new UMango.Core.UI.Homepage.HomepageManifest
            {
                Cards =
                [
                    new UMango.Core.UI.Homepage.HomepageCard
                    {
                        Title = "去下载页",
                        Action = "open-downloads",
                    },
                ],
            });

            var result = host.Load();

            Assert.Null(result.Error);
            Assert.Equal(0, result.DroppedCards);
            Assert.Single(result.Manifest!.Cards);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void 清单里的未注册动作卡片被丢弃()
    {
        var path = TempManifest();
        try
        {
            var host = new HomepageHost(path);
            host.Service.Save(new UMango.Core.UI.Homepage.HomepageManifest
            {
                Cards =
                [
                    new UMango.Core.UI.Homepage.HomepageCard
                    {
                        Title = "偷偷来的",
                        Action = "run-anything",
                    },
                ],
            });

            var result = host.Load();

            Assert.Equal(1, result.DroppedCards);
            Assert.Empty(result.Manifest!.Cards);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
