using UMango.Core.Download;
using UMango.Core.Terminal;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 下载器纯逻辑（<see cref="DownloaderSession"/>）的测试。
///
/// **为什么逻辑不放在 WPF 的 ViewModel 里**：本项目在 macOS 上开发，
/// 而 WPF 运行时在 macOS 上不存在 —— 测试工程一旦碰任何 WPF 类型，
/// 测试宿主就会要求 Microsoft.WindowsDesktop.App 并直接启动失败（实测过：
/// "You must install or update .NET to run this application. Framework:
///  'Microsoft.WindowsDesktop.App'"）。
/// 所以逻辑下沉到 Core，就能在本机被覆盖；WPF 侧只剩薄绑定。
/// </summary>
public class DownloaderSessionTests
{
    private sealed class FakeEngine(string id, bool available, params DownloadProtocol[] protocols) : IDownloadEngine
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<DownloadProtocol> Protocols => protocols;
        public string? ExecutablePath => available ? "/fake/" + id : null;
        public bool IsAvailable => available;
        public string LicenseNote => $"{id}: 不链接也不分发";

        /// <summary>录制收到的请求，便于断言"确实把链接交给了正确的引擎"</summary>
        public DownloadRequest? LastRequest { get; private set; }
        public bool WasCalled { get; private set; }
        public DownloadResult Next { get; set; } = new(true, "/tmp/x", null, null, 7);

        public Task<DownloadResult> DownloadAsync(DownloadRequest r, TerminalBridge? b,
            Action<DownloadProgress>? p, CancellationToken ct = default)
        {
            WasCalled = true;
            LastRequest = r;
            p?.Invoke(new DownloadProgress(0.5, 512, 1024, "1MiB", "3s", "gid1"));
            return Task.FromResult(Next);
        }
    }

    private static DownloaderSession Session(params IDownloadEngine[] engines)
        => new(new DownloadEngineRegistry(engines), Path.Combine(Path.GetTempPath(), "umango-sess-test"));

    [Fact]
    public void 空链接时提示粘贴且不可开始()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        Assert.Contains("粘贴", s.ProtocolHint);
        Assert.False(s.CanStart);
    }

    [Fact]
    public void http链接被识别并指出由哪个引擎处理()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        s.Url = "https://example.com/a.tgz";

        Assert.Equal(DownloadProtocol.Https, s.DetectedProtocol);
        Assert.Contains("HTTPS", s.ProtocolHint);
        Assert.Contains("aria2", s.ProtocolHint);
        Assert.True(s.CanStart);
    }

    [Fact]
    public void magnet链接识别为BitTorrent()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.BitTorrent));
        s.Url = "magnet:?xt=urn:btih:abcdef";
        Assert.Equal(DownloadProtocol.BitTorrent, s.DetectedProtocol);
        Assert.Contains("BitTorrent", s.ProtocolHint);
        Assert.True(s.CanStart);
    }

    [Fact]
    public void ed2k没有客户端时如实说明并禁用开始()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        s.Url = "ed2k://|file|x.iso|123|HASH|/";

        Assert.Equal(DownloadProtocol.Ed2k, s.DetectedProtocol);
        Assert.Null(s.SelectedEngine);
        Assert.Contains("未检测到", s.ProtocolHint);
        Assert.False(s.CanStart);
    }

    [Fact]
    public void 无法识别的链接给出明确提示()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        s.Url = "这不是链接";
        Assert.Contains("无法识别", s.ProtocolHint);
        Assert.False(s.CanStart);
    }

    [Fact]
    public void 引擎列表包含许可说明()
    {
        var s = Session(
            new FakeEngine("aria2", true, DownloadProtocol.Https),
            new FakeEngine("amule", false, DownloadProtocol.Ed2k));

        Assert.Equal(2, s.Engines.Count);
        Assert.All(s.Engines, e => Assert.False(string.IsNullOrWhiteSpace(e.LicenseNote)));
        Assert.Single(s.Engines, e => e.IsAvailable);
        Assert.Single(s.Engines, e => !e.IsAvailable);
    }

    [Fact]
    public async Task 开始下载会把请求交给选中的引擎并更新状态()
    {
        var engine = new FakeEngine("aria2", true, DownloadProtocol.Https);
        var s = Session(engine);
        s.Url = "  https://example.com/a.tgz  ";   // 前后空白应被去掉
        s.OutputDirectory = Path.Combine(Path.GetTempPath(), "umango-sess-out");

        var result = await s.StartAsync();

        Assert.True(result.Success);
        Assert.True(engine.WasCalled);
        Assert.Equal("https://example.com/a.tgz", engine.LastRequest!.Url);
        Assert.Equal(s.OutputDirectory, engine.LastRequest.OutputDirectory);
        Assert.False(s.IsBusy);
        Assert.NotNull(s.SavedPath);
        Assert.Null(s.ErrorMessage);
    }

    [Fact]
    public async Task 进度回调把状态翻译成中文文案()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        s.Url = "https://example.com/a.tgz";

        var seen = new List<string>();
        s.Changed += () => seen.Add(s.StatusMessage);

        await s.StartAsync();

        // 假引擎上报 50% → 过程中应出现"已下载 50%"
        Assert.Contains("已下载 50%", seen);
        // 结束后进度归 1
        Assert.Equal(1.0, s.ProgressFraction);
        Assert.True(s.IsProgressKnown);
    }

    [Fact]
    public async Task 拿不到进度时不伪造百分比而是显示已下载字节()
    {
        var noFraction = new NoFractionEngine();
        var s = Session(noFraction);
        s.Url = "https://example.com/a.tgz";

        var seen = new List<string>();
        s.Changed += () => seen.Add(s.StatusMessage);

        await s.StartAsync();

        Assert.Contains(seen, m => m.StartsWith("已下载 ") && m.EndsWith(" MiB"));
        Assert.DoesNotContain(seen, m => m.Contains('%'));
    }

    [Fact]
    public async Task 失败时记录错误码与消息()
    {
        var engine = new FakeEngine("aria2", true, DownloadProtocol.Https)
        {
            Next = new DownloadResult(false, null, "download_failed", "连接被拒绝", 3)
        };
        var s = Session(engine);
        s.Url = "https://example.com/a.tgz";

        var result = await s.StartAsync();

        Assert.False(result.Success);
        Assert.Equal("download_failed", result.ErrorCode);
        Assert.Equal("连接被拒绝", s.ErrorMessage);
        Assert.Equal("下载失败", s.StatusMessage);
    }

    [Fact]
    public async Task 条件不满足时不执行也不抛异常()
    {
        var engine = new FakeEngine("aria2", true, DownloadProtocol.Https);
        var s = Session(engine);
        s.Url = "";                       // 没填链接

        var result = await s.StartAsync();

        Assert.False(result.Success);
        Assert.Equal("not_ready", result.ErrorCode);
        Assert.False(engine.WasCalled);
        Assert.Equal("就绪", s.StatusMessage);   // 状态不被污染
    }

    [Fact]
    public void 默认下载目录落在用户Downloads下()
    {
        var dir = DownloaderSession.DefaultOutputDirectory();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.True(dir.EndsWith("UMango", StringComparison.Ordinal));
    }

    [Fact]
    public void 清空终端会清掉该通道的输出()
    {
        var s = Session(new FakeEngine("aria2", true, DownloadProtocol.Https));
        s.Bridge.Write(false, "hello");
        Assert.True(s.Bridge.Count > 0);

        s.ClearTerminal();
        Assert.Equal(0, s.Bridge.Count);
    }

    /// <summary>只上报字节、不报百分比的引擎（模拟服务端没给 Content-Length）。</summary>
    private sealed class NoFractionEngine : IDownloadEngine
    {
        public string Id => "nofraction";
        public string DisplayName => "nofraction";
        public IReadOnlyList<DownloadProtocol> Protocols => [DownloadProtocol.Https];
        public string? ExecutablePath => "/fake/nofraction";
        public bool IsAvailable => true;
        public string LicenseNote => "fake";

        public Task<DownloadResult> DownloadAsync(DownloadRequest r, TerminalBridge? b,
            Action<DownloadProgress>? p, CancellationToken ct = default)
        {
            p?.Invoke(new DownloadProgress(null, 2 * 1024 * 1024, null, null, null, null));
            return Task.FromResult(new DownloadResult(true, "/tmp/x", null, null, 1));
        }
    }
}
