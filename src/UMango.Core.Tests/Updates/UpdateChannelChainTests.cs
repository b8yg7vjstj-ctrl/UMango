using UMango.Core.App.Essentials;
using UMango.Core.Network;
using Xunit;

namespace UMango.Core.Tests.Updates;

/// <summary>
/// 多通道自更新（对应 PCL-CE 的 <c>Modules/Updates/</c> 架构，Apache-2.0 层的等价实现）。
/// </summary>
public class UpdateChannelChainTests
{
    /// <summary>可编程的假源。</summary>
    private sealed class FakeSource : IUpdateSource
    {
        public FakeSource(string name, bool available = true)
        {
            Name = name;
            IsAvailableResult = available;
        }

        public string Name { get; }
        public bool IsAvailableResult { get; set; }
        public int FetchCount { get; private set; }
        public bool ThrowOnFetch { get; set; }
        public UpdateCheckError? FailWith { get; set; }

        public bool IsAvailable() => IsAvailableResult;

        public Task<UpdateSourceResult> FetchAsync(
            UpdateChannelKind channel, string localVersion, CancellationToken ct = default)
        {
            FetchCount++;
            if (ThrowOnFetch) throw new InvalidOperationException("源内部炸了");
            if (FailWith is { } err)
                return Task.FromResult(UpdateSourceResult.Failed(err, $"{Name} 失败"));

            var remote = UMango.Core.Utils.SemVer.TryParse("9.9.9")!;
            var local = UMango.Core.Utils.SemVer.TryParse(localVersion)!;
            return Task.FromResult(UpdateSourceResult.Success(
                new UpdateInfo(remote, local, $"https://example.invalid/{Name}.pkg", 1024, "notes", false)));
        }
    }

    [Fact]
    public async Task 第一个源成功时_不会问后面的源()
    {
        var a = new FakeSource("A");
        var b = new FakeSource("B");
        var chain = new UpdateChannelChain().Add(a).Add(b);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.Equal("A", chain.WinnerSource);
        Assert.Equal(1, a.FetchCount);
        Assert.Equal(0, b.FetchCount); // 成功后必须停
    }

    [Fact]
    public async Task 首个源失败时_降级到第二个源()
    {
        var a = new FakeSource("A") { FailWith = UpdateCheckError.Network };
        var b = new FakeSource("B");
        var chain = new UpdateChannelChain().Add(a).Add(b);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.Equal("B", chain.WinnerSource);
        Assert.Single(chain.FailedSources);
        Assert.Equal("A", chain.FailedSources[0].Source);
    }

    [Fact]
    public async Task 不可用的源_被跳过且不产生网络请求()
    {
        var a = new FakeSource("A", available: false);
        var b = new FakeSource("B");
        var chain = new UpdateChannelChain().Add(a).Add(b);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, a.FetchCount);
        Assert.Contains("A", chain.SkippedSources);
    }

    [Fact]
    public async Task 源自己抛异常时_链子不崩_而是降级()
    {
        var a = new FakeSource("A") { ThrowOnFetch = true };
        var b = new FakeSource("B");
        var chain = new UpdateChannelChain().Add(a).Add(b);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.Equal("B", chain.WinnerSource);
        Assert.Single(chain.FailedSources);
        Assert.Equal(UpdateCheckError.Network, chain.FailedSources[0].Failure.Error);
    }

    [Fact]
    public async Task 全部源失败时_返回失败而不是抛异常()
    {
        var a = new FakeSource("A") { FailWith = UpdateCheckError.Network };
        var b = new FakeSource("B") { FailWith = UpdateCheckError.BadPayload };
        var chain = new UpdateChannelChain().Add(a).Add(b);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.False(result.IsSuccess);
        Assert.Null(chain.WinnerSource);
        Assert.Equal(2, chain.FailedSources.Count);
    }

    [Fact]
    public async Task 全部源都被跳过时_错误信息说明是本机没有可用源()
    {
        var a = new FakeSource("A", available: false);
        var chain = new UpdateChannelChain().Add(a);

        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.False(result.IsSuccess);
        Assert.Contains("没有可用的版本源", result.Failure!.Message);
    }

    [Fact]
    public async Task 没有任何源时_返回明确失败()
    {
        var chain = new UpdateChannelChain();
        var result = await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.False(result.IsSuccess);
        Assert.Contains("没有配置任何版本源", result.Failure!.Message);
    }

    [Fact]
    public async Task 连续两次解析_会清空上一次的状态()
    {
        var a = new FakeSource("A") { FailWith = UpdateCheckError.Network };
        var b = new FakeSource("B");
        var chain = new UpdateChannelChain().Add(a).Add(b);

        await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");
        await chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.Single(chain.FailedSources); // 不是累积成 2
        Assert.Equal("B", chain.WinnerSource);
    }

    [Fact]
    public async Task 取消信号会向上传播而不是被当成源失败()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var a = new FakeSource("A");
        var chain = new UpdateChannelChain().Add(a);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.ResolveAsync(UpdateChannelKind.Stable, "1.0.0", cts.Token));
    }

    [Fact]
    public void 预发布通道_接受预发布版()
    {
        Assert.True(UpdateChannelKind.Prerelease.AcceptsPrerelease());
        Assert.False(UpdateChannelKind.Stable.AcceptsPrerelease());
    }

    [Fact]
    public void 通道ID_与PCL一致()
    {
        Assert.Equal("stable", UpdateChannelKind.Stable.Id());
        Assert.Equal("prerelease", UpdateChannelKind.Prerelease.Id());
    }
}

/// <summary>静态清单源的测试。</summary>
public class StaticManifestUpdateSourceTests
{
    private static StaticManifestUpdateSource Source(string manifest, Func<string, bool>? matcher = null) =>
        new("清单", _ => Task.FromResult<string?>(manifest), matcher ?? (_ => true));

    [Fact]
    public async Task 合法清单_能解析出版本与下载地址()
    {
        var source = Source("""{"version":"1.2.3","url":"https://x/UMango-1.2.3.pkg","size":2048,"notes":"hi"}""");

        var result = await source.FetchAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.Equal("1.2.3", result.Info!.Version.ToString());
        Assert.Equal(2048, result.Info.FileSize);
        Assert.Equal("hi", result.Info.Notes);
    }

    [Fact]
    public async Task 正式通道_拒绝预发布版()
    {
        var source = Source("""{"version":"1.2.3-beta.1","url":"https://x/a.pkg"}""");

        var result = await source.FetchAsync(UpdateChannelKind.Stable, "1.0.0");

        Assert.False(result.IsSuccess);
        Assert.Equal(UpdateCheckError.BadVersion, result.Failure!.Error);
    }

    [Fact]
    public async Task 预发布通道_接受预发布版()
    {
        var source = Source("""{"version":"1.2.3-beta.1","url":"https://x/a.pkg"}""");

        var result = await source.FetchAsync(UpdateChannelKind.Prerelease, "1.0.0");

        Assert.True(result.IsSuccess);
        Assert.True(result.Info!.IsPreRelease);
    }

    [Fact]
    public async Task 非法JSON_返回BadPayload()
    {
        var result = await Source("<html>404</html>").FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.False(result.IsSuccess);
        Assert.Equal(UpdateCheckError.BadPayload, result.Failure!.Error);
    }

    [Fact]
    public async Task 缺version字段_返回BadPayload()
    {
        var result = await Source("""{"url":"https://x/a.pkg"}""").FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.Equal(UpdateCheckError.BadPayload, result.Failure!.Error);
    }

    [Fact]
    public async Task 缺url字段_返回BadPayload()
    {
        var result = await Source("""{"version":"1.2.3"}""").FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.Equal(UpdateCheckError.BadPayload, result.Failure!.Error);
    }

    [Fact]
    public async Task 版本号无法解析_返回BadVersion()
    {
        var result = await Source("""{"version":"garbage","url":"https://x/a.pkg"}""")
            .FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.Equal(UpdateCheckError.BadVersion, result.Failure!.Error);
    }

    [Fact]
    public async Task 安装包不匹配本平台_返回NoAsset()
    {
        var source = Source("""{"version":"1.2.3","url":"https://x/win-x64.pkg"}""", _ => false);
        var result = await source.FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.Equal(UpdateCheckError.NoAsset, result.Failure!.Error);
    }

    [Fact]
    public async Task 取清单时抛异常_被转成失败而不是抛出()
    {
        var source = new StaticManifestUpdateSource("清单", _ => throw new HttpRequestException("offline"), _ => true);
        var result = await source.FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.False(result.IsSuccess);
        Assert.Equal(UpdateCheckError.Network, result.Failure!.Error);
    }

    [Fact]
    public async Task 空清单_返回失败()
    {
        var result = await Source("").FetchAsync(UpdateChannelKind.Stable, "1.0.0");
        Assert.Equal(UpdateCheckError.Network, result.Failure!.Error);
    }
}
