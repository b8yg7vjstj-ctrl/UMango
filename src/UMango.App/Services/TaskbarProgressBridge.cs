using System;
using System.Windows;
using System.Windows.Shell;
using UMango.Core.App.Tasks;
using UMango.Core.Logging;

namespace UMango.App.Services;

/// <summary>
/// 把 <see cref="TaskCenter"/> 的聚合进度写进 Windows 任务栏图标。
///
/// **为什么需要**：PCL-CE 有任务栏进度聚合（下载/安装时任务栏图标上显示进度条）。
/// 这是"任务在后台跑、用户已切到别的窗口"时唯一的进度反馈渠道 —— 否则用户
/// 只能反复切回 UMango 看进度。
///
/// **平台边界**：WPF 的 <see cref="TaskbarItemInfo"/> 只在 Windows 上有效
/// （底层是 ITaskbarList3 COM 接口）。在 macOS/Linux 上构建时本类仍要能编译，
/// 因此对 <see cref="TaskbarItemInfo"/> 的访问集中在 <see cref="Apply"/> 一处，
/// 并由 <see cref="IsSupported"/> 在运行期短路。
/// </summary>
public sealed class TaskbarProgressBridge : IAsyncDisposable
{
    private readonly Window _window;
    private TaskProgressAggregator.TaskProgressWatcher? _watcher;

    public TaskbarProgressBridge(Window window)
    {
        _window = window;
    }

    /// <summary>
    /// 当前平台是否支持任务栏进度。
    /// Windows 上恒为 true；其它平台上调用 <see cref="Start"/> 只会记一行日志后返回。
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows();

    /// <summary>开始把聚合进度同步到任务栏。</summary>
    public void Start()
    {
        if (_watcher is not null) return;

        if (!IsSupported)
        {
            Log.Info("TaskbarProgress", "当前平台不支持任务栏进度，跳过");
            return;
        }

        _watcher = TaskProgressAggregator.Watch(OnSummary);
    }

    /// <summary>
    /// 把汇总写进任务栏。
    ///
    /// ⚠ 必须编组回 UI 线程：<see cref="TaskProgressAggregator.TaskProgressWatcher"/>
    /// 的回调来自 <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// 或 <c>CollectionChanged</c>，而两者都可能由**后台线程**触发（下载进度在下载线程上更新）。
    /// <see cref="TaskbarItemInfo"/> 是 DispatcherObject，跨线程直接写会抛
    /// InvalidOperationException —— 这个坑在 ToastHost 上已经踩过一次（见 MainWindow.ShowToast）。
    /// </summary>
    /// <summary>
    /// Core 的平台中立进度状态 → WPF 的 <see cref="TaskbarItemProgressState"/>。
    /// 判定逻辑本身在 <see cref="TaskProgressAggregator.MapToProgress"/>（那里有单元测试）。
    /// </summary>
    private static (TaskbarItemProgressState State, double Value) Translate(TaskProgressSummary summary)
    {
        var (state, value) = TaskProgressAggregator.MapToProgress(summary);
        var wpf = state switch
        {
            TaskbarProgressState.None => TaskbarItemProgressState.None,
            TaskbarProgressState.Indeterminate => TaskbarItemProgressState.Indeterminate,
            _ => TaskbarItemProgressState.Normal,
        };
        return (wpf, value);
    }

    private void OnSummary(TaskProgressSummary summary)
    {
        var dispatcher = _window.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnSummary(summary)));
            return;
        }

        var info = _window.TaskbarItemInfo;
        if (info is null) return;   // 未在 XAML 里挂 TaskbarItemInfo 时静默跳过

        var (state, value) = Translate(summary);
        info.ProgressState = state;
        info.ProgressValue = value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            await _watcher.DisposeAsync().ConfigureAwait(false);
            _watcher = null;
        }
    }
}
