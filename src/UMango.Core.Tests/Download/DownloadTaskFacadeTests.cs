using UMango.Core.Download;
using Xunit;

namespace UMango.Core.Tests.Download;

/// <summary>
/// 下载任务门面的测试：重试 + 镜像源降级。
///
/// 用一个可编程的假引擎来驱动，不碰网络。
/// </summary>
public class DownloadTaskFacadeTests
{
    /// <summary>可编程的假引擎：按 URL 决定成功/失败，并记录调用次数。</summary>
    private sealed class FakeEngine : IDownloadEngine
    {
        private readonly Func<string, int, DownloadResult> _behaviour;

        public FakeEngine(Func<string, int, DownloadResult> behaviour) => _behaviour = behaviour;

        public List<string> Calls { get; } = [];

        public string Id => "fake";
        public string DisplayName => "假引擎";
        public IReadOnlyList<DownloadProtocol> Protocols => [DownloadProtocol.Http, DownloadProtocol.Https];
        public string? ExecutablePath => null;
        public bool IsAvailable => true;
        public string LicenseNote => "测试用";

        public Task<DownloadResult> DownloadAsync(DownloadRequest request, UMango.Core.Terminal.TerminalBridge? bridge,
            Action<DownloadProgress>? onProgress, CancellationToken ct = default)
        {
            var n = Calls.Count(u => u == request.Url);
            Calls.Add(request.Url);
            return Task.FromResult(_behaviour(request.Url, n));
        }
    }

    private static DownloadResult Ok(string path) => new(true, path, null, null, 1);
    private static DownloadResult Fail(string code) => new(false, null, code, code, 1);

    private static DownloadEngineRegistry RegistryWith(params IDownloadEngine[] engines)
        => new(engines);

    private static DownloadRequest Request(string url = "https://a/f.bin")
        => new(url, "/tmp/out", "f.bin");

    [Fact]
    public async Task 主源成功时只用主源()
    {
        var engine = new FakeEngine((_, _) => Ok("/tmp/out/f.bin"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync([DownloadSource.Primary("https://a/f.bin")], Request());

        Assert.True(result.Success);
        Assert.Single(engine.Calls);
        Assert.Equal("主源", result.SucceededSourceName);
    }

    [Fact]
    public async Task 主源失败后换镜像并成功()
    {
        var engine = new FakeEngine((url, _) => url.Contains("mirror") ? Ok("/tmp/out/f.bin") : Fail("boom"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin"), DownloadSource.Mirror("https://mirror/f.bin", "镜像")],
            Request(), policy: DownloadRetryPolicy.None);

        Assert.True(result.Success);
        Assert.Equal(2, engine.Calls.Count);
        Assert.Equal("镜像", result.SucceededSourceName);
    }

    [Fact]
    public async Task 每个源都失败时返回最后一次的结果()
    {
        var engine = new FakeEngine((_, _) => Fail("boom"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin"), DownloadSource.Mirror("https://mirror/f.bin", "镜像")],
            Request(), policy: DownloadRetryPolicy.None);

        Assert.False(result.Success);
        Assert.Equal("boom", result.ErrorCode);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public async Task 取消导致的失败不重试()
    {
        var engine = new FakeEngine((_, _) => Fail("canceled"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin")], Request(),
            policy: new DownloadRetryPolicy { AttemptsPerSource = 5 });

        Assert.False(result.Success);
        // 取消了就该立刻停，不要继续重试
        Assert.Single(engine.Calls);
    }

    [Fact]
    public async Task 总尝试次数有上限()
    {
        var engine = new FakeEngine((_, _) => Fail("boom"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { MaxTotalAttempts = 3, DelayProvider = (_, _) => TimeSpan.Zero };

        await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin"), DownloadSource.Mirror("https://mirror/f.bin", "镜像")],
            Request(), policy: new DownloadRetryPolicy { AttemptsPerSource = 10 });

        // 关键：不能"主源试 10 次 + 镜像试 10 次"= 20 次
        Assert.Equal(3, engine.Calls.Count);
    }

    [Fact]
    public async Task 没有来源时给出明确错误()
    {
        var facade = new DownloadTaskFacade(RegistryWith(new FakeEngine((_, _) => Ok("x")))) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync([], Request());

        Assert.False(result.Success);
        Assert.Equal("no_source", result.ErrorCode);
    }

    [Fact]
    public async Task 空白的来源会被忽略()
    {
        var engine = new FakeEngine((_, _) => Ok("/tmp/out/f.bin"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin"), DownloadSource.Primary("   ")], Request());

        Assert.True(result.Success);
        Assert.Single(engine.Calls);
    }

    [Fact]
    public async Task 每次尝试都会回调()
    {
        var engine = new FakeEngine((url, _) => url.Contains("mirror") ? Ok("/tmp/out/f.bin") : Fail("boom"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };
        var seen = new List<DownloadAttempt>();

        await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin"), DownloadSource.Mirror("https://mirror/f.bin", "镜像")],
            Request(), onAttempt: seen.Add, policy: DownloadRetryPolicy.None);

        Assert.Equal(2, seen.Count);
        Assert.False(seen[0].Success);
        Assert.True(seen[1].Success);
    }

    [Fact]
    public async Task 尝试记录里带来源名与轮次()
    {
        var engine = new FakeEngine((_, _) => Fail("boom"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync(
            [DownloadSource.Primary("https://a/f.bin")], Request(),
            policy: new DownloadRetryPolicy { AttemptsPerSource = 2 });

        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal("主源", result.Attempts[0].Source.Name);
        Assert.Equal(1, result.Attempts[0].Attempt);
        Assert.Equal(2, result.Attempts[1].Attempt);
    }

    [Fact]
    public async Task 便捷重载只用主源()
    {
        var engine = new FakeEngine((_, _) => Ok("/tmp/out/f.bin"));
        var facade = new DownloadTaskFacade(RegistryWith(engine)) { DelayProvider = (_, _) => TimeSpan.Zero };

        var result = await facade.DownloadAsync("https://a/f.bin", Request());

        Assert.True(result.Success);
        Assert.Single(engine.Calls);
    }
}

/// <summary>镜像地址派生。</summary>
public class MirrorUrlsTests
{
    [Fact]
    public void 主源排在第一位()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["https://{url}"]);

        Assert.Equal("主源", sources[0].Name);
        Assert.Equal("https://a/f.bin", sources[0].Url);
    }

    [Fact]
    public void 模板里的占位符被替换()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["https://mirror.example/{url}"]);

        Assert.Equal(2, sources.Count);
        Assert.Equal("https://mirror.example/https://a/f.bin", sources[1].Url);
    }

    [Fact]
    public void 没有占位符的模板被拒绝()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["https://evil.example/other"]);

        // 绝不能出现一个与请求无关的地址
        Assert.Single(sources);
    }

    [Fact]
    public void 空白模板被忽略()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["", "   ", "https://m/{url}"]);

        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public void 与主源相同的派生地址不重复加入()
    {
        // 模板就是 "{url}" 本身 → 派生结果与主源完全一样，不该重复
        var sources = MirrorUrls.Build("https://a/f.bin", ["{url}"]);

        Assert.Single(sources);
    }

    [Fact]
    public void 镜像名取自给定名称()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["https://m/{url}"], ["我的镜像"]);

        Assert.Equal("我的镜像", sources[1].Name);
    }

    [Fact]
    public void 没给名字时用域名()
    {
        var sources = MirrorUrls.Build("https://a/f.bin", ["https://m.example.com/{url}"]);

        Assert.Equal("m.example.com", sources[1].Name);
    }

    [Fact]
    public void 规范地址为空时返回空列表()
    {
        Assert.Empty(MirrorUrls.Build("", ["https://m/{url}"]));
    }
}
