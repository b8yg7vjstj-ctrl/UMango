using System;
using System.Threading.Tasks;
using UMango.Core.App.Essentials;
using UMango.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 设置页的「检查更新」卡片。
///
/// 独立成一个 VM（而不是塞进 SettingsViewModel）的理由：
/// ① SettingsViewModel 已有 262 行，再塞一个带 5 个状态属性的更新流程会难读；
/// ② 更新流程有自己的生命周期（检查中→有新版本→下载中→重启），
///    与主题/动画/记录这些"改完即生效"的设置项不同步。
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    public UpdateViewModel()
    {
        CurrentVersionText = UMango.Shell.Services.UpdateService.LocalVersion;
    }

    /// <summary>本地版本号（构造时取一次，运行中不变）。</summary>
    public string CurrentVersionText { get; }

    /// <summary>状态文案（由 <see cref="SetStatus"/> 统一写，保证按钮文案与提示同步）。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>是否正在检查/下载（按钮禁用态）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>是否发现新版本（控制「立即更新」按钮的可见性）。</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    private UpdateInfo? _pending;

    /// <summary>
    /// 检查更新。失败只在状态行提示，不弹窗 —— 检查更新是用户主动行为，
    /// 且失败多半是网络问题（GitHub 不可达），弹窗会打扰。
    /// </summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetStatus(L("Settings.Update.Checking"));

        try
        {
            var result = await UMango.Shell.Services.AppServices.Update.CheckAsync();

            switch (result)
            {
                case UpdateInfo info when info.IsNewer:
                    _pending = info;
                    HasUpdate = true;
                    SetStatus(L("Settings.Update.Found", info.Version.Original));
                    break;
                case UpdateInfo:
                    SetStatus(L("Settings.Update.Latest"));
                    break;
                case UpdateCheckFailure failure:
                    SetStatus(L("Settings.Update.Failed", failure.Message));
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 下载并应用更新。下载完成后启动助手进程并退出本进程。
    /// </summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy || _pending is null) return;
        IsBusy = true;
        SetStatus(L("Settings.Update.Downloading"));

        try
        {
            var pkg = await UMango.Shell.Services.AppServices.Update.DownloadAsync(
                _pending.DownloadUrl,
                p => SetStatus(L("Settings.Update.DownloadProgress", (int)Math.Round(p * 100))));

            var error = UMango.Shell.Services.AppServices.Update.LaunchApply(pkg);
            if (error is not null)
            {
                SetStatus(L("Settings.Update.ApplyFailed", error.Message));
                HasUpdate = false;
                return;
            }

            SetStatus(L("Settings.Update.Restarting"));
            // 助手进程已在后台启动，本进程该让位了。
            // 直接 Environment.Exit 而不是 Window.Close：关窗不会退出进程（还有后台任务）。
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            SetStatus(L("Settings.Update.DownloadFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string text) => StatusMessage = text;
}
