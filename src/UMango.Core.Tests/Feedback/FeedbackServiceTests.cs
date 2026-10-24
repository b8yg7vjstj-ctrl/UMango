using System.Text.Json;
using UMango.Core.Feedback;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 反馈页的测试（用假 HttpService，不联网）。
/// </summary>
public sealed class FeedbackServiceTests
{
    private sealed class FakeHttp : IHttpService
    {
        private readonly string? _json;
        private readonly bool _ok;

        public FakeHttp(string? json, bool ok = true)
        {
            _json = json;
            _ok = ok;
        }

        public string? ProxyUrl { get; set; }
        public int TimeoutMs { get; set; }
        public string UserAgent { get; set; } = "test";

        public Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default)
        {
            if (!_ok)
                return Task.FromResult(new HttpResponse<T> { IsSuccess = false, Error = "网络错误", StatusCode = 500 });

            var value = JsonSerializer.Deserialize<T>(_json!, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return Task.FromResult(new HttpResponse<T> { IsSuccess = true, Value = value, StatusCode = 200 });
        }

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static FeedbackService NewService(string? json, bool ok = true, FeedbackTagMap? tags = null,
        Func<FeedbackDiagnosticInfo>? diag = null)
        => new(new FakeHttp(json, ok), tags, diag);

    private const string SampleJson = """
        {
          "items": [
            {
              "title": "安装 npm 包失败",
              "body": "提示 404",
              "number": "12",
              "user": { "login": "alice" },
              "created_at": "2026-01-02T03:04:05Z",
              "html_url": "https://example.test/12",
              "labels": [ { "id": "1", "name": "处理中" } ]
            },
            {
              "title": "进度条不刷新",
              "body": "只有 Windows 上复现",
              "number": "13",
              "labels": [ { "id": "2", "name": "已完成" } ]
            },
            {
              "title": "没有标签的反馈",
              "body": "随便说说",
              "number": "14",
              "labels": []
            }
          ]
        }
        """;

    [Fact]
    public async Task 未配置源时返回无源状态而不是失败()
    {
        var service = NewService(SampleJson);

        var result = await service.FetchAsync(null);

        Assert.Equal(FeedbackFetchStatus.NoSource, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task 空白源同样按无源处理()
    {
        var service = NewService(SampleJson);

        var result = await service.FetchAsync("   ");

        Assert.Equal(FeedbackFetchStatus.NoSource, result.Status);
    }

    [Fact]
    public async Task 抓取失败时返回失败状态与错误信息()
    {
        var service = NewService(null, ok: false);

        var result = await service.FetchAsync("https://example.test/issues");

        Assert.Equal(FeedbackFetchStatus.Failed, result.Status);
        Assert.Equal("网络错误", result.Error);
    }

    [Fact]
    public async Task 抓取成功时返回全部条目()
    {
        var service = NewService(SampleJson);

        var result = await service.FetchAsync("https://example.test/issues");

        Assert.Equal(FeedbackFetchStatus.Ok, result.Status);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task 抓取成功时解析标题正文与链接()
    {
        var service = NewService(SampleJson);

        var result = await service.FetchAsync("https://example.test/issues");

        var first = result.Items[0];
        Assert.Equal("安装 npm 包失败", first.Title);
        Assert.Equal("提示 404", first.Content);
        Assert.Equal("12", first.Id);
        Assert.Equal("alice", first.User?.Login);
        Assert.Equal("https://example.test/12", first.Url);
    }

    [Fact]
    public async Task 抓取时丢弃既无标题也无正文的空条目()
    {
        var json = """
            { "items": [ { "title": "", "body": "" }, { "title": "有标题", "body": "" } ] }
            """;

        var service = NewService(json);

        var result = await service.FetchAsync("https://example.test/issues");

        Assert.Single(result.Items);
        Assert.Equal("有标题", result.Items[0].Title);
    }

    [Fact]
    public void 标签名匹配出分类()
    {
        var map = new FeedbackTagMap();

        var entry = new FeedbackEntry { Labels = [new FeedbackLabel { Name = "处理中" }] };

        Assert.Equal(FeedbackCategory.Processing, map.Classify(entry));
    }

    [Fact]
    public void 标签名匹配大小写不敏感()
    {
        var map = new FeedbackTagMap();

        var entry = new FeedbackEntry { Labels = [new FeedbackLabel { Name = "COMPLETED" }] };

        Assert.Equal(FeedbackCategory.Completed, map.Classify(entry));
    }

    [Fact]
    public void 无匹配标签时归为未分类()
    {
        var map = new FeedbackTagMap();

        var entry = new FeedbackEntry { Labels = [new FeedbackLabel { Name = "随便什么" }] };

        Assert.Equal(FeedbackCategory.Uncategorized, map.Classify(entry));
    }

    [Fact]
    public void 显式声明的type优先于标签()
    {
        var map = new FeedbackTagMap();

        var entry = new FeedbackEntry
        {
            Type = new FeedbackType { Name = "Pause" },
            Labels = [new FeedbackLabel { Name = "处理中" }]
        };

        Assert.Equal(FeedbackCategory.Pause, map.Classify(entry));
    }

    [Fact]
    public void 可注册自定义标签映射()
    {
        var map = new FeedbackTagMap();
        map.Register("需要复现", FeedbackCategory.WaitingProcess);

        var entry = new FeedbackEntry { Labels = [new FeedbackLabel { Name = "需要复现" }] };

        Assert.Equal(FeedbackCategory.WaitingProcess, map.Classify(entry));
    }

    [Fact]
    public void 注册空白标签被忽略()
    {
        var map = new FeedbackTagMap();
        map.Register("   ", FeedbackCategory.Pause);

        var entry = new FeedbackEntry { Labels = [new FeedbackLabel { Name = "   " }] };

        Assert.Equal(FeedbackCategory.Uncategorized, map.Classify(entry));
    }

    [Theory]
    [InlineData("Processing", FeedbackCategory.Processing)]
    [InlineData("processing", FeedbackCategory.Processing)]
    [InlineData("等待处理", FeedbackCategory.WaitingProcess)]
    [InlineData("已完成", FeedbackCategory.Completed)]
    public void 分类名可从英文或中文解析(string name, FeedbackCategory expected)
    {
        Assert.True(FeedbackTagMap.TryParseCategory(name, out var category));
        Assert.Equal(expected, category);
    }

    [Fact]
    public void 无法识别的分类名解析失败()
    {
        Assert.False(FeedbackTagMap.TryParseCategory("胡说八道", out var category));
        Assert.Equal(FeedbackCategory.Uncategorized, category);
    }

    [Fact]
    public void 分组按注册顺序排列且未分类在最后()
    {
        var service = NewService(SampleJson);
        var items = new[]
        {
            new FeedbackEntry { Title = "c", Labels = [new FeedbackLabel { Name = "已完成" }] },
            new FeedbackEntry { Title = "u", Labels = [] },
            new FeedbackEntry { Title = "p", Labels = [new FeedbackLabel { Name = "处理中" }] }
        };

        var groups = service.Group(items);

        Assert.Equal(3, groups.Count);
        Assert.Equal(FeedbackCategory.Processing, groups[0].Category);
        Assert.Equal(FeedbackCategory.Completed, groups[1].Category);
        Assert.Equal(FeedbackCategory.Uncategorized, groups[2].Category);
    }

    [Fact]
    public void 分组内保持抓取顺序()
    {
        var service = NewService(SampleJson);
        var items = new[]
        {
            new FeedbackEntry { Title = "first", Labels = [new FeedbackLabel { Name = "处理中" }] },
            new FeedbackEntry { Title = "second", Labels = [new FeedbackLabel { Name = "处理中" }] }
        };

        var groups = service.Group(items);

        Assert.Single(groups);
        Assert.Equal("first", groups[0].Items[0].Title);
        Assert.Equal("second", groups[0].Items[1].Title);
    }

    [Fact]
    public void 空列表分组结果为空()
    {
        var service = NewService(SampleJson);

        var groups = service.Group([]);

        Assert.Empty(groups);
    }

    [Fact]
    public void 诊断信息标题以反馈前缀开头()
    {
        var service = NewService(SampleJson, diag: () => new FeedbackDiagnosticInfo
        {
            Content = "系统信息",
            Lines = ["UMango 1.0", "Windows 11"]
        });

        var info = service.BuildDiagnosticInfo();

        Assert.Equal("[反馈] ", info.Title);
        Assert.Equal("系统信息", info.Content);
        Assert.Equal(2, info.Lines.Count);
    }

    [Fact]
    public void 诊断信息没有正文时用信息行拼接()
    {
        var service = NewService(SampleJson, diag: () => new FeedbackDiagnosticInfo
        {
            Lines = ["UMango 1.0", "Windows 11"]
        });

        var info = service.BuildDiagnosticInfo();

        Assert.Contains("UMango 1.0", info.Content);
        Assert.Contains("Windows 11", info.Content);
    }
}
