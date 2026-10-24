using UMango.App.ViewModels;
using UMango.Core.Feedback;
using UMango.Core.Network;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 反馈页 ViewModel 的测试。
///
/// 用假 <see cref="IHttpService"/>，不联网。
/// </summary>
public sealed class FeedbackViewModelTests
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
            var value = System.Text.Json.JsonSerializer.Deserialize<T>(_json!,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Task.FromResult(new HttpResponse<T> { IsSuccess = true, Value = value, StatusCode = 200 });
        }

        public Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private const string SampleJson = """
        {
          "items": [
            { "title": "A", "body": "a", "labels": [ { "name": "处理中" } ] },
            { "title": "B", "body": "b", "labels": [ { "name": "已完成" } ] }
          ]
        }
        """;

    private static FeedbackViewModel NewVm(string? json, bool ok = true)
        => new(
            new FeedbackService(
                new FakeHttp(json, ok),
                new FeedbackTagMap(),
                // 诊断信息由 App 侧提供；测试里给一份固定内容，
                // 否则默认的（空）诊断信息会让「草稿已填入」这条断言没有意义。
                () => new FeedbackDiagnosticInfo { Content = "系统信息", Lines = ["UMango 1.0"] }),
            sourceOverride: "https://example.test/issues");

    [Fact]
    public async Task 加载成功后按分类产生分组()
    {
        var vm = NewVm(SampleJson);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal(FeedbackCategory.Processing, vm.Groups[0].Category);
        Assert.Equal(FeedbackCategory.Completed, vm.Groups[1].Category);
    }

    [Fact]
    public async Task 加载失败时给出错误且不产生分组()
    {
        var vm = NewVm(null, ok: false);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task 加载完成后草稿已填入诊断信息()
    {
        var vm = NewVm(SampleJson);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.DraftTitle);
        Assert.NotEmpty(vm.DraftBody);
    }

    [Fact]
    public async Task 重新加载会替换而不是累加分组()
    {
        var vm = NewVm(SampleJson);

        await vm.LoadCommand.ExecuteAsync(null);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Groups.Count);
    }

    [Fact]
    public async Task 加载期间不重复并发执行()
    {
        var vm = NewVm(SampleJson);

        await Task.WhenAll(vm.LoadCommand.ExecuteAsync(null), vm.LoadCommand.ExecuteAsync(null));

        Assert.Equal(2, vm.Groups.Count);
    }

    [Fact]
    public async Task 手动填入诊断信息会更新草稿()
    {
        var vm = NewVm(SampleJson);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.DraftBody = "我自己写的描述";
        vm.FillDiagnosticsCommand.Execute(null);

        Assert.DoesNotContain("我自己写的描述", vm.DraftBody);
    }
}
