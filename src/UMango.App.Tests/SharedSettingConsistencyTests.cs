using UMango.Shell.ViewModels;
using UMango.Core.Configuration;
using UMango.Core.Terminal;
using Xunit;
using UMango.Shell.Services;

namespace UMango.App.Tests;

/// <summary>
/// **跨页面共享设置的一致性**测试。
///
/// 调试模式被两个页面同时使用（设置页的开关、进度页的开关），而它背后有三层：
///   1. Core 的静态开关 <see cref="TerminalBridge.DebugMode"/>（终端桥**运行时**的读取点）
///   2. 设置服务里的 `debug.terminalMode`（**持久化**权威）
///   3. 两个 ViewModel 的属性（界面绑定）
/// 这三层任何一处脱节，都会表现为"这里打开了、那里还是关的，重启后又变回去"。
/// 这类问题很难靠肉眼发现，所以用测试钉住。
/// </summary>
[Collection("静态状态")]
public class SharedSettingConsistencyTests : IDisposable
{
    private readonly bool _original;

    public SharedSettingConsistencyTests() => _original = AppSettings.DebugMode;

    public void Dispose() => AppSettings.DebugMode = _original;

    [Fact]
    public void 进度页改开关会同时写到静态开关与设置服务()
    {
        var vm = new ProgressViewModel();
        vm.DebugMode = !_original;

        Assert.Equal(!_original, TerminalBridge.DebugMode);                          // 运行时读取点
        Assert.Equal(!_original, AppSettings.Settings.Get<bool>(UiSettings.DebugModeKey));  // 持久化
        Assert.Equal(!_original, vm.DebugMode);                                      // 界面绑定
    }

    [Fact]
    public void 设置页改开关会同步到进度页读到同一个值()
    {
        var settingsVm = new SettingsViewModel();
        var progressVm = new ProgressViewModel();

        settingsVm.DebugMode = !_original;

        // 两个页面的属性是"同一个真相"的不同视图，必须一致
        Assert.Equal(settingsVm.DebugMode, progressVm.DebugMode);
        Assert.Equal(!_original, TerminalBridge.DebugMode);
    }

    [Fact]
    public void 进度页改开关后设置页也能读到()
    {
        var settingsVm = new SettingsViewModel();
        var progressVm = new ProgressViewModel();

        progressVm.DebugMode = !_original;

        Assert.Equal(progressVm.DebugMode, settingsVm.DebugMode);
    }

    [Fact]
    public void 重复设置同一个值不会产生额外变更()
    {
        var vm = new ProgressViewModel();
        vm.DebugMode = _original;   // 与当前一致

        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.DebugMode)) changes++; };
        vm.DebugMode = _original;

        Assert.Equal(0, changes);
    }

    [Fact]
    public void 设置服务的默认值与Core静态开关的默认值一致()
    {
        // 两侧各写一份默认值是最容易漂移的地方。
        // 未显式设置过时，描述符里的默认值应当就是 Core 静态开关的默认值。
        var descriptorDefault = UiSettings.All
            .First(d => d.Key == UiSettings.DebugModeKey).DefaultValue;
        Assert.Equal(false, descriptorDefault);
    }
}
