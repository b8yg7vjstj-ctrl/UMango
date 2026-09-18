using UMango.App.ViewModels;
using UMango.Core.Download;
using UMango.Core.Terminal;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 下载器 ViewModel 测试（薄绑定层）。
///
/// 纯逻辑（协议识别、引擎选择、状态文案）已在 Core 的 DownloaderSession 里测过；
/// 这里专测**这一层自己的职责**：属性转发、命令可用性、线程编组与终端行收集。
/// </summary>
public class DownloaderViewModelTests
{
    private sealed class FakeEngine(string id, bool available, params DownloadProtocol[] protocols) : IDownloadEngine
    {
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<DownloadProtocol> Protocols => protocols;
        public string? ExecutablePath => available ? "/fake/" + id : null;
        public bool IsAvailable => available;
        public string LicenseNote => $"{id}: 不链接也不分发";

        public DownloadResult Next { get; set; } = new(true, "/tmp/x", null, null, 5);
        public Action<DownloadProgress>? LastOnProgress { get; private set; }

        public Task<DownloadResult> DownloadAsync(DownloadRequest r, TerminalBridge? b,
            Action<DownloadProgress>? p, CancellationToken ct = default)
        {
            LastOnProgress = p;
            b?.Write(false, "引擎输出一行");
            p?.Invoke(new DownloadProgress(0.42, 420, 1000, "1MiB", "3s", "g1"));
            return Task.FromResult(Next);
        }
    }

    private static (DownloaderViewModel Vm, FakeEngine Engine, DownloaderSession Session) Build(
        bool available = true, DownloadProtocol protocol = DownloadProtocol.Https)
    {
        var engine = new FakeEngine("aria2", available, protocol);
        var session = new DownloaderSession(
            new DownloadEngineRegistry([engine]),
            Path.Combine(Path.GetTempPath(), "umango-vm-test"));
        return (new DownloaderViewModel(session), engine, session);
    }

    [Fact]
    public void 属性转发到Session()
    {
        var (vm, _, _) = Build();
        Assert.Equal("就绪", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressKnown);
        Assert.Null(vm.ProgressFraction);
        Assert.NotEmpty(vm.ProtocolHint);
    }

    [Fact]
    public void 引擎列表带许可说明()
    {
        var (vm, _, _) = Build();
        Assert.Single(vm.Engines);
        Assert.Contains("不链接", vm.Engines[0].LicenseNote);
    }

    [Fact]
    public void 设置Url后提示与命令可用性同步更新()
    {
        var (vm, _, _) = Build();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Url = "https://example.com/a.tgz";

        // 这三个属性必须跟着变，否则界面上的提示与按钮状态会和实际脱节
        Assert.Contains(nameof(vm.ProtocolHint), changed);
        Assert.Contains(nameof(vm.CanStart), changed);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public void 不可用引擎时不可开始()
    {
        var (vm, _, _) = Build(available: false);
        vm.Url = "https://example.com/a.tgz";
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void ed2k链接在没有eD2k引擎时不可开始()
    {
        var (vm, _, _) = Build(protocol: DownloadProtocol.Https);   // 只有 aria2
        vm.Url = "ed2k://|file|x.iso|1|H|/";
        Assert.False(vm.CanStart);
        Assert.Contains("eD2k", vm.ProtocolHint);
    }

    [Fact]
    public async Task 开始下载会走完整流程并在过程中转发进度()
    {
        var (vm, engine, _) = Build();
        vm.Url = "https://example.com/a.tgz";
        vm.OutputDirectory = Path.Combine(Path.GetTempPath(), "umango-vm-out");

        // 捕获状态文案的**变化序列**：不能只看最终值 ——
        // 完成后状态会被覆写成"下载完成"，中间态会消失（第一版断言就错在这里）。
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusMessage)) statuses.Add(vm.StatusMessage);
        };

        await vm.StartAsync();

        Assert.Contains("已下载 42%", statuses);        // 过程里的进度文案确实转发了
        Assert.False(vm.IsBusy);
        Assert.True(engine.LastOnProgress is not null);
        Assert.NotNull(vm.SavedPath);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(1.0, vm.ProgressFraction);         // 完成后归 1
        Assert.True(vm.IsProgressKnown);
    }

    [Fact]
    public async Task 终端输出被收集到集合里()
    {
        var (vm, _, _) = Build();
        vm.Url = "https://example.com/a.tgz";

        await vm.StartAsync();

        // 引擎写的那一行应当出现在界面的终端集合里（无 WPF 时 UiDispatch 会直接执行）
        Assert.Contains(vm.TerminalLines, l => l.Text == "引擎输出一行");
        Assert.NotEmpty(vm.TerminalLines);
    }

    [Fact]
    public async Task 清空终端会同时清掉集合与桥()
    {
        var (vm, _, session) = Build();
        vm.Url = "https://example.com/a.tgz";
        await vm.StartAsync();
        Assert.NotEmpty(vm.TerminalLines);

        vm.ClearTerminalCommand.Execute(null);

        Assert.Empty(vm.TerminalLines);
        Assert.Equal(0, session.Bridge.Count);
    }

    [Fact]
    public async Task 失败时把错误写到ErrorMessage()
    {
        var (vm, engine, _) = Build();
        engine.Next = new DownloadResult(false, null, "download_failed", "连接被拒绝", 2);
        vm.Url = "https://example.com/a.tgz";

        await vm.StartAsync();

        Assert.Equal("连接被拒绝", vm.ErrorMessage);
        Assert.Equal("下载失败", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void 刷新引擎命令可用()
    {
        var (vm, _, _) = Build();
        vm.RefreshEnginesCommand.Execute(null);
        Assert.NotEmpty(vm.Engines);
    }

    [Fact]
    public async Task 条件不满足时开始不做任何事()
    {
        var (vm, engine, _) = Build();
        vm.Url = "";   // 没填链接

        await vm.StartAsync();

        Assert.False(vm.IsBusy);
        Assert.Equal("就绪", vm.StatusMessage);   // 状态不被污染
        Assert.Empty(vm.TerminalLines);
    }
}
