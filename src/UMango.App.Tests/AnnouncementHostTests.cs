using UMango.App.Services;
using UMango.Core.Announcements;
using UMango.Core.Configuration;
using UMango.Core.Network;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 公告动作的执行路径（App 侧第二道防线）。
///
/// 守的是「**远程公告里的动作名必须先注册才能执行**」这条边界。
/// Core 侧的白名单判定是第一道，这里是第二道：
/// 即使有人绕过 Core 直接调 InvokeAction，未注册的名字也必须什么也不做。
/// </summary>
public class AnnouncementHostTests
{
    private static AnnouncementHost NewHost(out SettingsService settings)
    {
        settings = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(settings);
        return new AnnouncementHost(new AnnouncementService(new NullHttp(), settings));
    }

    [Fact]
    public void 未注册的动作不会被执行()
    {
        var host = NewHost(out _);
        var called = false;
        host.NavigationRequested += _ => called = true;

        var ok = host.InvokeAction("run-anything", null);

        Assert.False(ok);
        Assert.False(called);
    }

    [Fact]
    public void 已注册的动作会触发导航()
    {
        var host = NewHost(out _);
        string? target = null;
        host.NavigationRequested += t => target = t;

        var ok = host.InvokeAction("open-settings", null);

        Assert.True(ok);
        Assert.Equal("settings", target);
    }

    [Fact]
    public void 动作名匹配忽略大小写()
    {
        var host = NewHost(out _);
        string? target = null;
        host.NavigationRequested += t => target = t;

        Assert.True(host.InvokeAction("OPEN-SETTINGS", null));
        Assert.Equal("settings", target);
    }

    [Fact]
    public void 登记了名字但没有实现的动作_不会执行也不会抛异常()
    {
        // 注册表是唯一真相：名字在、但没人实现（Handler 为 null）时，
        // 点击必须安静地什么都不做 —— 不能抛 NullReferenceException。
        var host = NewHost(out _);
        host.Service.Actions.Register(new AnnouncementActionDescriptor("未实现", "还没有接线"));

        var ok = host.InvokeAction("未实现", null);

        Assert.False(ok);
    }

    [Fact]
    public void 空动作名不会被执行()
    {
        var host = NewHost(out _);
        var called = false;
        host.NavigationRequested += _ => called = true;

        Assert.False(host.InvokeAction("", null));
        Assert.False(host.InvokeAction("   ", null));
        Assert.False(called);
    }

    [Fact]
    public async Task 公告源留空时不发请求()
    {
        var host = NewHost(out var settings);
        settings.Set(UiSettings.AnnouncementSourceKey, "");

        var result = await host.LoadAsync();

        Assert.Equal(AnnouncementFetchStatus.NoSource, result.Status);
    }

    [Fact]
    public void 标记已展示_之后不再返回()
    {
        var host = NewHost(out _);
        var announcement = new Announcement { Id = "x", Title = "标题" };

        host.MarkShown([announcement]);

        Assert.Equal(["x"], host.Service.ReadShowed());
    }

    [Fact]
    public void 清空已展示_记录被清掉()
    {
        var host = NewHost(out _);
        host.MarkShown([new Announcement { Id = "x" }]);

        host.ResetShown();

        Assert.Empty(host.Service.ReadShowed());
    }

    /// <summary>不发任何请求的假 HTTP。</summary>
    private sealed class NullHttp : IHttpService
    {
        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; } = 1000;
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default)
            => Task.FromResult(new HttpResponse<T> { IsSuccess = false, Error = "不应发起请求" });

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new HttpResponse<string> { IsSuccess = false, Error = "不应发起请求" });
    }
}
