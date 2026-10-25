// SPDX-License-Identifier: Apache-2.0
//
// WPF 侧的灰度模式施加器（把 Shell 的 <see cref="IGrayModeApplier"/> 接到
// WPF 专属的 <c>GrayModeController</c> 上）。
//
// 为什么在 App 层：脱色要改 WPF 资源字典里的画刷，是 Windows 专属的操作；
// 平台中立的「灰度开关写进设置项」那一半在 Shell 的 SettingsViewModel 里。

using UMango.Shell.Capabilities;

namespace UMango.App.Themes;

/// <summary>
/// WPF 灰度模式施加器。
///
/// <para>
/// **刻意不直接碰 <c>Application.Current</c>**：那会让 Shell 层的 ViewModel
/// 引用 WPF 类型，在 macOS 的测试宿主里加载 PresentationFramework 时崩。
/// 资源字典由外壳注入。
/// </para>
/// </summary>
public sealed class WpfGrayModeApplier : IGrayModeApplier
{
    private readonly Func<System.Windows.ResourceDictionary?> _resources;

    /// <summary>创建施加器。</summary>
    /// <param name="resources">取目标资源字典的委托（通常返回 <c>App.GrayResources</c>）。</param>
    public WpfGrayModeApplier(Func<System.Windows.ResourceDictionary?> resources)
        => _resources = resources;

    /// <summary>开启或关闭灰度模式。</summary>
    public void SetEnabled(bool enabled)
    {
        var controller = App.Gray;
        if (controller is null) return;
        controller.SetEnabled(enabled, _resources());
    }
}
