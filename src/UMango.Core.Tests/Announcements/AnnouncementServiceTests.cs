using UMango.Core.Announcements;
using UMango.Core.Configuration;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests.Announcements;

/// <summary>公告服务的测试。</summary>
public class AnnouncementServiceTests
{
    /// <summary>
    /// 可编程的假 HTTP 服务。
    ///
    /// 直接按泛型契约实现（返回 JSON 字符串再反序列化），
    /// 这样和真实 <see cref="HttpService"/> 的行为一致 —— 调用方拿到的
    /// 就是真正被反序列化过的对象，而不是测试里手搓的强转。
    /// </summary>
    private sealed class FakeHttp : IHttpService
    {
        private readonly Func<string, (bool Ok, string? Json, string? Error)> _handler;

        public FakeHttp(Func<string, (bool, string?, string?)> handler) => _handler = handler;

        public FakeHttp(string json) : this(_ => (true, json, null)) { }

        public List<string> RequestedUrls { get; } = [];

        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; } = 1000;
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default)
        {
            RequestedUrls.Add(url);
            var (ok, json, error) = _handler(url);
            if (!ok || json is null)
                return Task.FromResult(new HttpResponse<T> { IsSuccess = false, Error = error });

            T? value;
            try
            {
                value = System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new HttpResponse<T> { IsSuccess = false, Error = "解析失败：" + ex.Message });
            }
            return Task.FromResult(new HttpResponse<T> { IsSuccess = true, Value = value });
        }

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new HttpResponse<string> { IsSuccess = true, Value = "" });
    }

    private static (SettingsService Settings, MemorySettingsStore Store) NewSettings()
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        UiSettings.RegisterAll(settings);
        return (settings, store);
    }

    private static AnnouncementService NewService(IHttpService http, SettingsService settings)
        => new(http, settings);

    private static Announcement NewAnnouncement(string id, string title = "标题")
        => new() { Id = id, Title = title, Detail = "正文" };

    private const string OneAnnouncementJson =
        """{"content":[{"title":"标题","detail":"正文","id":"a","date":"2026-01-01"}]}""";

    private const string TwoAnnouncementsJson =
        """{"content":[{"title":"A","detail":"正文","id":"a"},{"title":"B","detail":"正文","id":"b"}]}""";

    [Fact]
    public async Task 没有配置源_不发起任何请求()
    {
        var (settings, _) = NewSettings();
        var http = new FakeHttp("{\"content\":[]}");
        var service = NewService(http, settings);

        var result = await service.LoadAsync();

        Assert.Equal(AnnouncementFetchStatus.NoSource, result.Status);
        Assert.Empty(http.RequestedUrls);
        Assert.False(result.HasNew);
    }

    [Fact]
    public async Task 配置源_拉取并返回全部公告()
    {
        var (settings, _) = NewSettings();
        settings.Set(UiSettings.AnnouncementSourceKey, "https://example.test/ann.json");
        var http = new FakeHttp(TwoAnnouncementsJson);
        var service = NewService(http, settings);

        var result = await service.LoadAsync();

        Assert.Equal(AnnouncementFetchStatus.Ok, result.Status);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.HasNew);
        Assert.Equal(["https://example.test/ann.json"], http.RequestedUrls);
    }

    [Fact]
    public async Task 已展示过的_不再返回()
    {
        var (settings, _) = NewSettings();
        settings.Set(UiSettings.AnnouncementSourceKey, "https://example.test/ann.json");
        var http = new FakeHttp(TwoAnnouncementsJson);
        var service = NewService(http, settings);

        service.MarkShown([NewAnnouncement("a")]);
        var result = await service.LoadAsync();

        Assert.Single(result.Items);
        Assert.Equal("b", result.Items[0].Id);
    }

    [Fact]
    public async Task 展示之后_再次拉取_不再返回()
    {
        var (settings, _) = NewSettings();
        settings.Set(UiSettings.AnnouncementSourceKey, "https://example.test/ann.json");
        var http = new FakeHttp(OneAnnouncementJson);
        var service = NewService(http, settings);

        var first = await service.LoadAsync();
        service.MarkShown(first.Items);
        var second = await service.LoadAsync();

        Assert.Single(first.Items);
        Assert.Empty(second.Items);
        Assert.False(second.HasNew);
    }

    [Fact]
    public void 标记已展示_落到设置里()
    {
        var (settings, _) = NewSettings();
        var service = NewService(new FakeHttp("{\"content\":[]}"), settings);

        service.MarkShown([NewAnnouncement("a"), NewAnnouncement("b")]);

        Assert.Equal(["a", "b"], service.ReadShowed().Order());
    }

    [Fact]
    public void 重置已展示_清空记录()
    {
        var (settings, _) = NewSettings();
        var service = NewService(new FakeHttp("{\"content\":[]}"), settings);
        service.MarkShown([NewAnnouncement("a")]);

        service.ResetShown();

        Assert.Empty(service.ReadShowed());
    }

    [Fact]
    public void 空的公告id_不会被记住()
    {
        // 回归护栏：id 为空的公告如果被记住，等于所有空 id 公告共用一个"已读"槽位 ——
        // 一条空 id 公告被读之后，别的空 id 公告就再也不会显示了。
        var (settings, _) = NewSettings();
        var service = NewService(new FakeHttp("{\"content\":[]}"), settings);

        service.MarkShown([new Announcement { Id = "", Title = "无 id" }, new Announcement { Id = "  ", Title = "空白 id" }]);

        Assert.Empty(service.ReadShowed());
    }

    [Fact]
    public void 标记数量超上限_只保留最近的()
    {
        var (settings, _) = NewSettings();
        var service = NewService(new FakeHttp("{\"content\":[]}"), settings);

        var many = Enumerable.Range(0, AnnouncementService.MaxRemembered + 20)
            .Select(i => NewAnnouncement("id" + i))
            .ToList();
        service.MarkShown(many);

        var showed = service.ReadShowed();
        Assert.Equal(AnnouncementService.MaxRemembered, showed.Count);
        // 保留的是最近的那些
        Assert.Contains("id" + (AnnouncementService.MaxRemembered + 19), showed);
        Assert.DoesNotContain("id0", showed);
    }

    [Fact]
    public async Task 抓取失败_返回失败状态而不是抛异常()
    {
        var (settings, _) = NewSettings();
        settings.Set(UiSettings.AnnouncementSourceKey, "https://example.test/ann.json");
        var http = new FakeHttp(_ => (false, null, "网络错误"));
        var service = NewService(http, settings);

        var result = await service.LoadAsync();

        Assert.Equal(AnnouncementFetchStatus.Failed, result.Status);
        Assert.Equal("网络错误", result.Error);
        Assert.False(result.HasNew);
    }

    [Fact]
    public async Task 抓取抛异常_被吞掉并转成失败()
    {
        var (settings, _) = NewSettings();
        settings.Set(UiSettings.AnnouncementSourceKey, "https://example.test/ann.json");
        var http = new FakeHttp(_ => throw new InvalidOperationException("炸了"));
        var service = NewService(http, settings);

        var result = await service.LoadAsync();

        Assert.Equal(AnnouncementFetchStatus.Failed, result.Status);
        Assert.Contains("炸了", result.Error);
    }

    [Fact]
    public void 动作白名单_未注册的一律拒绝()
    {
        var registry = new AnnouncementActionRegistry();
        registry.Register(new AnnouncementActionDescriptor("open-downloads", "打开下载页"));

        Assert.True(registry.IsRegistered("open-downloads"));
        Assert.False(registry.IsRegistered("run-anything"));
        Assert.False(registry.IsRegistered(""));
        Assert.Null(registry.Find("run-anything"));
    }

    [Fact]
    public void 动作白名单_匹配忽略大小写()
    {
        // 与主页清单的注册表口径一致（同一套"具名动作"不该有两套大小写规则）。
        var registry = new AnnouncementActionRegistry();
        registry.Register(new AnnouncementActionDescriptor("Open-Downloads", "打开下载页"));

        Assert.True(registry.IsRegistered("Open-Downloads"));
        Assert.True(registry.IsRegistered("open-downloads"));
        Assert.True(registry.IsRegistered("OPEN-DOWNLOADS"));
    }

    [Fact]
    public void 动作白名单_默认是空的()
    {
        // 安全默认值：什么都不注册 → 什么都不许触发
        var registry = new AnnouncementActionRegistry();
        Assert.Empty(registry.All);
        Assert.False(registry.IsRegistered("任何动作"));
    }

    [Fact]
    public void 动作描述符_可以要求二次确认()
    {
        var descriptor = new AnnouncementActionDescriptor("danger", "危险操作", requiresConfirmation: true);
        Assert.True(descriptor.RequiresConfirmation);
    }
}
