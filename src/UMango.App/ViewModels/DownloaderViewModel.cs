using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.Download;
using UMango.Core.Terminal;

namespace UMango.App.ViewModels;

/// <summary>
/// 下载器页的 ViewModel —— **只是 Core 侧 <see cref="DownloaderSession"/> 的一层薄绑定**。
///
/// 逻辑（协议识别、引擎选择、能否开始、进度语义、状态文案）全部在 Core 里，
/// 原因见 <see cref="DownloaderSession"/> 的注释：WPF 运行时在 macOS 上不存在，
/// 任何碰 WPF 类型的代码都无法在本机被单测覆盖。把逻辑留在这一层就等于放弃测试。
///
/// 本类只负责三件事：
///   1. 把 session 的状态转发成可观察属性（界面绑定用）；
///   2. 把 RelayCommand 接到 session 的方法上；
///   3. **线程编组**：session 的事件可能来自 CLI 的 stdout 读取线程，必须回到 UI 线程。
/// </summary>
public sealed class DownloaderViewModel : INotifyPropertyChanged
{
    private readonly DownloaderSession _session;

    public DownloaderViewModel(DownloaderSession? session = null)
    {
        _session = session ?? new DownloaderSession(Services.AppServices.DownloadEngines);
        _session.Changed += OnSessionChanged;
        _session.Bridge.LineAppended += OnLine;

        _session.RefreshEngines();

        StartCommand = new RelayCommand(async () => await _session.StartAsync(), () => _session.CanStart);
        CancelCommand = new RelayCommand(_session.Cancel, () => _session.IsBusy);
        ClearTerminalCommand = new RelayCommand(() => { TerminalLines.Clear(); _session.ClearTerminal(); });
        RefreshEnginesCommand = new RelayCommand(() => { _session.RefreshEngines(); });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---------- 转发给界面的状态 ----------

    public string Url
    {
        get => _session.Url;
        set { _session.Url = value; Raise(); Raise(nameof(ProtocolHint)); Raise(nameof(CanStart)); RefreshCommands(); }
    }

    public string OutputDirectory
    {
        get => _session.OutputDirectory;
        set { _session.OutputDirectory = value; Raise(); }
    }

    public string ProtocolHint => _session.ProtocolHint;
    public bool IsBusy => _session.IsBusy;
    public double? ProgressFraction => _session.ProgressFraction;
    public bool IsProgressKnown => _session.IsProgressKnown;
    public string StatusMessage => _session.StatusMessage;
    public string? SpeedText => _session.SpeedText;
    public string? EtaText => _session.EtaText;
    public string? ErrorMessage => _session.ErrorMessage;
    public string? SavedPath => _session.SavedPath;
    public bool CanStart => _session.CanStart;

    /// <summary>引擎列表（含许可说明）</summary>
    public IReadOnlyList<DownloadEngineInfo> Engines => _session.Engines;

    public ObservableCollection<TerminalLine> TerminalLines { get; } = [];

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearTerminalCommand { get; }
    public ICommand RefreshEnginesCommand { get; }

    // ---------- 线程编组 ----------

    /// <summary>
    /// 把动作编组到 UI 线程。
    /// 没有 Application（例如单测环境）时直接执行 —— 无界面时也就无所谓线程亲和性。
    /// </summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return; }
        dispatcher.Invoke(action);
    }

    private void OnSessionChanged() => OnUi(() =>
    {
        Raise(nameof(IsBusy));
        Raise(nameof(ProgressFraction));
        Raise(nameof(IsProgressKnown));
        Raise(nameof(StatusMessage));
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
        Raise(nameof(ErrorMessage));
        Raise(nameof(SavedPath));
        Raise(nameof(CanStart));
        Raise(nameof(ProtocolHint));
        Raise(nameof(Engines));
        RefreshCommands();
    });

    private void OnLine(TerminalLine line) => OnUi(() => TerminalLines.Add(line));

    private void RefreshCommands()
    {
        (StartCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
}
