// SPDX-License-Identifier: Apache-2.0
//
// 平台中立的「主题切换编排层」。
//
// 为什么要有它：主题切换这件事要做三件事 —— ① 改状态、② 让宿主换资源字典、
// ③ 按「跟随系统」策略解析深浅色。前两件里「换资源字典」是各壳自己的事，
// 第三件（探测系统主题）在 WPF 侧要读注册表、Avalonia 侧要问 Avalonia，
// 也各壳不同。把这层放到 Shell，界面逻辑（设置页）就与壳无关了。

using UMango.Core.UI.Theme;

namespace UMango.Shell.Theming;

/// <summary>系统深浅色探测（各壳提供自己的实现）。</summary>
public interface ISystemThemeProbe
{
    /// <summary>探测系统主题。读不到时返回 <see cref="SystemThemeState.Unknown"/>。</summary>
    SystemThemeState Detect();
}

/// <summary>
/// 主题切换的**编排层**（两个壳共用）。
///
/// <para>
/// 状态本体在 <see cref="ShellThemeState"/>；本类负责把「用户要切主题」翻译成
/// 「状态变了 + 宿主换资源字典 + 动效预设跟着走」。
/// </para>
///
/// <para>
/// 宿主（WPF / Avalonia）通过 <see cref="HostApply"/> 挂上自己换资源字典的逻辑；
/// 若没挂（无 UI 宿主、测试），状态照样变，只是不换字典。
/// </para>
/// </summary>
public static class ShellThemeController
{
    /// <summary>
    /// 宿主换资源字典的回调（参数：主题族 + 是否深色）。
    ///
    /// <para>
    /// 由各壳在启动时挂一次。**刻意不用事件**：事件可以被多方订阅，
    /// 换资源字典是**排他**的 —— 挂两个订阅者就会出现"两个壳各换一次"。
    /// </para>
    /// </summary>
    public static Action<ThemeKind, bool>? HostApply { get; set; }

    /// <summary>系统主题探测器（各壳提供自己的实现）。没挂时按「探测不到」处理。</summary>
    public static ISystemThemeProbe? SystemThemeProbe { get; set; }

    /// <summary>主题已切换（状态已变、宿主字典已换）。自绘内容可挂这里。</summary>
    public static event Action? ThemeChanged;

    /// <summary>当前主题族。</summary>
    public static ThemeKind CurrentKind => ShellThemeState.Current.Kind;

    /// <summary>当前是否为深色。</summary>
    public static bool IsDark => ShellThemeState.Current.IsDark;

    /// <summary>
    /// 切换主题：改状态 + 让宿主换资源字典。
    ///
    /// <para>
    /// 动效预设随主题同步（需求：「动效之类的在 PCL 主题中完整保留」）——
    /// 只写 profile 一个键，读改写会覆盖用户单独改过的其它动画设置。
    /// </para>
    /// </summary>
    public static void Apply(ThemeKind kind, bool dark)
    {
        ShellThemeState.Current.Apply(kind, dark);

        HostApply?.Invoke(kind, dark);

        WriteAnimationProfile(kind);

        ThemeChanged?.Invoke();
    }

    /// <summary>在当前主题族上切换深浅色。</summary>
    public static void ToggleDark() => Apply(CurrentKind, !IsDark);

    /// <summary>
    /// 按跟随策略应用深浅色。
    ///
    /// 「跟随系统」时用探测结果；探测不到就沿用当前深浅色，不把界面切成随机状态。
    /// </summary>
    public static void ApplyFollowMode(ThemeFollowMode mode)
    {
        // **只在真的需要探测时才探测**：策略被强制成深/浅时结果已经确定，
        // 再去问一次系统是白花钱 —— 探测在 macOS/Linux 上要跑外部进程
        // （defaults / gsettings），每次切主题都跑一遍会让界面卡一下。
        var system = mode == ThemeFollowMode.System
            ? SystemThemeProbe?.Detect() ?? UMango.Core.UI.Theme.SystemThemeState.Unknown
            : UMango.Core.UI.Theme.SystemThemeState.Unknown;

        var resolved = UMango.Core.UI.Theme.SystemThemeResolver.Resolve(mode, system, IsDark);
        Apply(CurrentKind, resolved);
    }

    /// <summary>把主题族映射成动效预设（PCL 经典 → PCL 动效，Fluent → Fluent 动效）。</summary>
    private static void WriteAnimationProfile(ThemeKind kind)
    {
        Services.AppSettings.Settings.Set(
            Core.Configuration.UiSettings.AnimationProfileKey,
            kind == ThemeKind.PclClassic
                ? Core.Animation.MotionProfileKind.Pcl
                : Core.Animation.MotionProfileKind.Fluent);
    }
}
