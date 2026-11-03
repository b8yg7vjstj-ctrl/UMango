using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace UMango.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        // 外壳能力注册：把 Avalonia 侧的实现装进 Shell 的能力注册表，
        // 让 Shell 层的 ViewModel（设置页/音乐页/反馈页）在两个壳里都能用。
        UMango.Shell.Services.ShellCapabilities.Clipboard = new Services.AvaloniaClipboardService();
        UMango.Shell.Services.ShellCapabilities.GrayModeApplier = new Services.AvaloniaGrayModeApplier();
        UMango.Shell.Services.ShellCapabilities.MusicOutput = CreateMusicOutput();

        // 对话框宿主：安装前的二次确认（DownloadViewModel.ConfirmInstallAsync）
        // 经 PclUiBridge 找宿主；不注册的话该壳会按"不阻断"处理，等于确认开关无效。
        UMango.Shell.Services.PclUiBridge.DialogHost = new Services.AvaloniaDialogHost();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 创建 Avalonia 侧的音频输出后端。
    ///
    /// <para>
    /// 当前是**明确降级**的空实现（Avalonia 侧还没接音频库）——
    /// 曲库与播放状态机在 Core 里是可用的，只是「把 PCM 送出去」这一步没接上。
    /// </para>
    /// </summary>
    private static UMango.Shell.Capabilities.IMusicOutput CreateMusicOutput()
    {
        var library = new UMango.Core.Media.Music.MusicLibrary();
        var controller = new UMango.Core.Media.Music.MusicPlaybackController(library);
        return new Services.AvaloniaMusicOutput(library, controller);
    }
}
