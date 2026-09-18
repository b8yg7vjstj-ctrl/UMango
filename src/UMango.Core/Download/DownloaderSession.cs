using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载器的**纯逻辑**（对应 PCL-CE 的「百宝箱」下载功能）。
///
/// **为什么放在 Core 而不是 WPF 的 ViewModel 里**：
/// 本项目在 macOS 上开发，而 WPF 运行时在 macOS 上**不存在** ——
/// 一旦测试工程碰了任何 WPF 类型（ICommand / Application.Dispatcher 等），
/// 测试宿主就会要求 Microsoft.WindowsDesktop.App 并直接启动失败（实测过）。
/// 因此「协议识别 / 引擎选择 / 能否开始 / 进度语义 / 状态文案」这些**纯逻辑**
/// 全部放在这里，用不依赖 WPF 的类型表达，于是可以在 macOS 上被单测覆盖；
/// WPF 侧的 ViewModel 只做一层薄绑定。
///
/// 本类刻意不使用任何 WPF 类型：
///   · 用 <see cref="Changed"/> 事件代替 INotifyPropertyChanged 的自动实现；
///   · 用 <see cref="CancellationTokenSource"/> 代替命令的 CanExecute；
///   · 终端输出直接走 Core 的 <see cref="TerminalBridge"/>。
/// </summary>
public sealed class DownloaderSession
{
    private readonly DownloadEngineRegistry _engines;
    private CancellationTokenSource? _cts;

    public DownloaderSession(DownloadEngineRegistry registry, string? outputDirectory = null)
    {
        _engines = registry;
        _bridge = TerminalBridge.For(ChannelId);
        OutputDirectory = outputDirectory ?? DefaultOutputDirectory();

        // 构造时就填好引擎列表：这是 Core 侧自身完整可用的前提。
        // （曾漏掉这一步 —— 引擎列表一直为空，只有 WPF 侧的 ViewModel 记得调 RefreshEngines，
        //   于是 Core 的测试与任何非 WPF 调用方都看不到引擎。由单测抓出。）
        RefreshEngines();
    }

    /// <summary>该下载器在终端桥里的通道名</summary>
    public const string ChannelId = "downloader";

    private readonly TerminalBridge _bridge;

    /// <summary>状态发生变化时触发。界面订阅它刷新绑定（WPF 侧需自行编组回 UI 线程）。</summary>
    public event Action? Changed;

    public TerminalBridge Bridge => _bridge;

    // ---------- 输入 ----------

    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (_url == value) return;
            _url = value ?? "";
            Raise();
        }
    }

    // 赋初值只是为了让编译器确认非空：真正的默认值在构造函数里按平台算（见 DefaultOutputDirectory）
    private string _outputDirectory = string.Empty;
    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (_outputDirectory == value) return;
            _outputDirectory = value ?? "";
            Raise();
        }
    }

    // ---------- 派生状态 ----------

    /// <summary>当前链接的协议</summary>
    public DownloadProtocol DetectedProtocol => DownloadProtocolExtensions.Detect(Url);

    /// <summary>当前链接会由哪个引擎处理；null 表示没有可用引擎</summary>
    public IDownloadEngine? SelectedEngine =>
        string.IsNullOrWhiteSpace(Url) ? null : _engines.Select(Url);

    /// <summary>
    /// 给用户的一句提示：识别成什么协议、由谁来下、或者为什么不能下。
    /// 用户不必知道 magnet 要走 BitTorrent、ed2k 要走 aMule。
    /// </summary>
    public string ProtocolHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url)) return "粘贴 http / https / ftp / magnet / ed2k 链接";

            var protocol = DetectedProtocol;
            if (protocol == DownloadProtocol.Unknown) return "无法识别该链接的协议";

            var engine = SelectedEngine;
            if (engine is null)
            {
                return protocol == DownloadProtocol.Ed2k
                    ? "识别为 eD2k，但未检测到 eD2k 客户端（aMule / mldonkey）"
                    : $"识别为 {protocol.DisplayName()}，但没有可用引擎";
            }
            return $"识别为 {protocol.DisplayName()}，将由 {engine.DisplayName} 下载";
        }
    }

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(Url) && SelectedEngine is not null;

    // ---------- 运行状态 ----------

    public bool IsBusy { get; private set; }

    /// <summary>0~1；**为 null 表示拿不到进度**（界面不要把它当成 0%）</summary>
    public double? ProgressFraction { get; private set; }

    /// <summary>是否拿得到确定的进度</summary>
    public bool IsProgressKnown => ProgressFraction is not null;

    public string StatusMessage { get; private set; } = "就绪";
    public string? SpeedText { get; private set; }
    public string? EtaText { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SavedPath { get; private set; }
    public long? BytesDone { get; private set; }
    public long? BytesTotal { get; private set; }

    /// <summary>引擎信息（含许可说明，界面需要展示）</summary>
    public IReadOnlyList<DownloadEngineInfo> Engines { get; private set; } = [];

    /// <summary>刷新引擎列表（用户可能中途装了 aria2）</summary>
    public void RefreshEngines()
    {
        Engines = _engines.All.Select(e => new DownloadEngineInfo(
            e.Id, e.DisplayName, e.IsAvailable,
            string.Join(" / ", e.Protocols.Select(x => x.DisplayName())),
            e.LicenseNote, e.ExecutablePath)).ToList();
        Raise();
    }

    /// <summary>平台默认下载目录</summary>
    public static string DefaultOutputDirectory()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads)
            ? Path.Combine(downloads, "UMango")
            : Path.Combine(Path.GetTempPath(), "UMango");
    }

    // ---------- 执行 ----------

    /// <summary>开始下载。返回结果；未满足条件时返回失败而不抛异常。</summary>
    public async Task<DownloadResult> StartAsync(CancellationToken ct = default)
    {
        if (!CanStart)
        {
            return new DownloadResult(false, null, "not_ready",
                string.IsNullOrWhiteSpace(Url) ? "请先填写链接" : ProtocolHint, 0);
        }

        var engine = SelectedEngine!;

        ErrorMessage = null;
        SavedPath = null;
        ProgressFraction = null;
        SpeedText = null;
        EtaText = null;
        BytesDone = null;
        BytesTotal = null;
        IsBusy = true;
        StatusMessage = $"正在用 {engine.DisplayName} 下载…";
        _bridge.Clear();
        Raise();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var result = await _engines.DownloadAsync(
                new DownloadRequest(Url.Trim(), OutputDirectory),
                _bridge,
                OnProgress,
                _cts.Token).ConfigureAwait(false);

            if (result.Success)
            {
                SavedPath = result.OutputPath;
                ProgressFraction = 1.0;
                // eD2k 这类"已投递给客户端"的场景要靠 Message 说明，不能只说"完成"
                StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? $"下载完成（用时 {result.DurationMs / 1000.0:0.0}s）"
                    : result.Message;
            }
            else
            {
                ErrorMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? $"下载失败（{result.ErrorCode}）"
                    : result.Message;
                StatusMessage = "下载失败";
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            return new DownloadResult(false, null, "canceled", "下载已取消", 0);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "下载失败";
            return new DownloadResult(false, null, "unexpected", ex.Message, 0);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            Raise();
        }
    }

    /// <summary>取消当前下载。</summary>
    public void Cancel()
    {
        if (_cts is null) return;
        _cts.Cancel();
        StatusMessage = "正在取消…";
        Raise();
    }

    /// <summary>清空终端输出。</summary>
    public void ClearTerminal()
    {
        _bridge.Clear();
        Raise();
    }

    private void OnProgress(DownloadProgress p)
    {
        ProgressFraction = p.Fraction;
        SpeedText = p.SpeedText;
        EtaText = p.EtaText;
        BytesDone = p.BytesDone;
        BytesTotal = p.BytesTotal;

        // 拿不到百分比时显示已下载字节数，**不编一个百分比出来**
        StatusMessage = p.Fraction is { } f
            ? $"已下载 {f * 100:0}%"
            : p.BytesDone is { } bytes
                ? $"已下载 {bytes / 1024.0 / 1024.0:0.0} MiB"
                : "下载中";

        Raise();
    }

    private void Raise() => Changed?.Invoke();
}

/// <summary>供界面展示的引擎信息（含可用性与**许可说明**）。</summary>
public sealed record DownloadEngineInfo(
    string Id,
    string DisplayName,
    bool IsAvailable,
    string ProtocolsText,
    string LicenseNote,
    string? ExecutablePath);
