// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的主题运行时切换器 —— 与 WPF 侧 `UMango.App.Themes.ThemeManager` 同构。
//
// 为什么需要它：
//   Avalonia 的 `{DynamicResource}` 在资源字典被替换后会重新求值，
//   但**必须真的替换字典**才会触发 —— 这与 WPF 的机制一致，
//   所以两边用同一套做法：四套 token 字典 + 运行时整块换掉 MergedDictionaries。
//
// 与 WPF 侧的对应关系：
//   WPF  : App.xaml 里 <ResourceDictionary.MergedDictionaries> 放 token 字典，
//          切换时移除旧的、追加新的（后加入者优先）。
//   Avalonia: Application.Resources.MergedDictionaries 同理，
//          追加新的 IResourceDictionary 实例（ResourceInclude 是延迟加载的，
//          但切主题时我们直接 new 一个已解析好的字典，避免异步竞态）。
//
// 为什么 token 字典是「生成产物」：
//   WPF 侧那四份 .xaml 是**逐像素校验过的权威定义**（数值取自 PCL-CE，
//   深色主题的 G3/G4 还做过 WCAG AA 对比度提升）。Avalonia 侧由
//   `scripts/sync-theme-tokens.py` 从 WPF 侧单向生成，
//   保证两个壳的主题 token 永远不会漂移。
//
// 差异（与 WPF 侧）：
//   ① WPF 用 pack URI（ResourceDictionary.Source）延迟加载；
//      Avalonia 侧改成**直接 new ResourceDictionary** 并逐项塞值 ——
//      这样切主题是同步完成的，不存在「旧字典还在异步加载」的中间态。
//   ② WPF 侧把「动效预设随主题同步」写在 Apply() 里；
//      Avalonia 侧同样做（Shell 层的 AppSettings 是共享的，行为必须一致）。
//
// 许可：设计语义与 PCL-CE 无关（PCL 的主题切换在受限许可目录里，
// 这里只做「四个 token 字典 + 整块替换」这一通用做法）。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using UMango.Core.Animation;
using UMango.Core.Configuration;
using UMango.Shell.Services;

namespace UMango.Avalonia.Themes;

/// <summary>
/// 可选主题族。深浅色是同一族的第二个维度。
///
/// 枚举本体在 Shell 层（<c>UMango.Shell.Theming.ThemeKind</c>），两个壳共用一份。
/// </summary>
using ThemeKind = UMango.Shell.Theming.ThemeKind;
using UMango.Shell.Theming;

/// <summary>
/// Avalonia 侧的主题运行时切换器。
///
/// 用法：
/// <code>
/// AvaloniaThemeManager.Apply(ThemeKind.Fluent10, dark: true);
/// AvaloniaThemeManager.ToggleDark();
/// </code>
/// </summary>
public static class AvaloniaThemeManager
{
    /// <summary>token 字典在 Themes 目录内的相对路径（与 WPF 侧同名同结构）。</summary>
    public const string LightPclPath = "Themes/Tokens.Light.Pcl.axaml";
    public const string DarkPclPath = "Themes/Tokens.Dark.Pcl.axaml";
    public const string LightFluent10Path = "Themes/Tokens.Light.Fluent10.axaml";
    public const string DarkFluent10Path = "Themes/Tokens.Dark.Fluent10.axaml";

    /// <summary>
    /// 当前主题族，默认 PCL 经典。
    ///
    /// <para>
    /// 状态的**唯一真相源**在 Shell 层（<see cref="ShellThemeState"/>）—— WPF 与
    /// Avalonia 两个壳共用同一份，两个壳里的「深色」永远指同一个东西。
    /// </para>
    /// </summary>
    public static ThemeKind CurrentKind => ShellThemeState.Current.Kind;

    /// <summary>当前是否为深色。默认浅色（与 WPF 侧一致）。</summary>
    public static bool IsDark => ShellThemeState.Current.IsDark;

    /// <summary>当前主题族（WPF 侧同名成员的对应物）。</summary>
    public static ThemeKind Current => CurrentKind;

    /// <summary>主题换过之后触发；自绘内容可以挂这里。</summary>
    public static event Action? ThemeChanged;

    /// <summary>按主题族 + 深浅解析出 token 字典相对路径。</summary>
    public static string ResolveRelativePath(ThemeKind kind, bool dark) => (kind, dark) switch
    {
        (ThemeKind.PclClassic, false) => LightPclPath,
        (ThemeKind.PclClassic, true) => DarkPclPath,
        (ThemeKind.Fluent10, false) => LightFluent10Path,
        _ => DarkFluent10Path,
    };

    /// <summary>
    /// 切换主题：把旧的 token 字典从 <c>Application.Current.Resources.MergedDictionaries</c>
    /// 移除，再插入新的一套。页面 XAML 一律用 <c>{DynamicResource}</c>，不需要重建。
    /// </summary>
    public static void Apply(ThemeKind kind, bool dark)
    {
        // 主题切换的**编排**在 Shell 层（状态、动效预设、跟随系统策略），
        // Avalonia 这一侧只负责「换哪份资源字典」。与 WPF 壳走同一条路径。
        EnsureHooked();
        UMango.Shell.Theming.ShellThemeController.Apply(kind, dark);
    }

    /// <summary>把「换资源字典」的回调挂到 Shell 编排层上（只挂一次）。</summary>
    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        UMango.Shell.Theming.ShellThemeController.HostApply = SwapDictionaries;
    }

    private static bool _hooked;

    /// <summary>
    /// 把 Avalonia 的 token 资源字典换成指定主题那一份。
    ///
    /// <para>
    /// 这一步是 Avalonia 专属的（<see cref="IResourceDictionary"/> + avares URI），
    /// 因此留在 Avalonia 层；状态与策略在 Shell 层。
    /// </para>
    /// </summary>
    private static void SwapDictionaries(ThemeKind kind, bool dark)
    {
        var app = Application.Current;

        // 设计时 / 单元测试里可能没有 Application：不换字典，不抛异常。
        if (app is null)
        {
            ThemeChanged?.Invoke();
            return;
        }

        // 资源字典只能在 UI 线程上换。
        // 注意：这里 post 的是 **SwapDictionaries 自己**，不是 Apply ——
        // 若 post Apply，会让 Shell 编排层再跑一遍（写两次动效预设、派发两次事件）。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SwapDictionaries(kind, dark));
            return;
        }

        var dictionary = BuildTokenDictionary(kind, dark);

        var merged = app.Resources.MergedDictionaries;

        // 倒序移除旧的 token 字典（按标记识别），避免边遍历边删导致索引错位。
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is AvaloniaTokenDictionary)
            {
                merged.RemoveAt(i);
            }
        }

        // 追加到末尾：Avalonia 的资源查找同样是后加入者优先。
        merged.Add(dictionary);

        ThemeChanged?.Invoke();
    }

    /// <summary>在当前主题族上切换浅色/深色。</summary>
    public static void ToggleDark() => Apply(CurrentKind, !IsDark);

    /// <summary>
    /// 按跟随策略应用深浅色。「跟随系统」时读系统探测结果；
    /// 探测不到就沿用当前深浅色，不把界面切成随机状态。
    /// </summary>
    public static void ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode mode)
    {
        EnsureHooked();
        UMango.Shell.Theming.ShellThemeController.SystemThemeProbe ??= SystemThemeProbe.AsInterface;
        UMango.Shell.Theming.ShellThemeController.ApplyFollowMode(mode);
    }

    /// <summary>
    /// 构造 token 字典。
    ///
    /// 用 <see cref="AvaloniaXamlLoader"/> 加载 avares URI 里的 .axaml，
    /// 而不是手写一份「哪些键是什么值」的清单 —— 后者会随 WPF 侧 token 变化而失效
    /// （而 WPF 侧那份是有注释、有历史依据的权威定义）。
    /// </summary>
    private static IResourceDictionary BuildTokenDictionary(ThemeKind kind, bool dark)
    {
        var uri = new Uri($"avares://UMango/{ResolveRelativePath(kind, dark)}");
        var loaded = AvaloniaXamlLoader.Load(uri);

        if (loaded is IResourceDictionary dictionary)
        {
            return new AvaloniaTokenDictionary(dictionary);
        }

        throw new InvalidOperationException($"主题 token 字典不是资源字典：{uri}");
    }

    /// <summary>
    /// 包一层，用来标记「这是 token 字典」。
    ///
    /// 不能靠文件名识别（那是 WPF 的做法，因为那边是 pack URI 延迟加载的）；
    /// Avalonia 侧我们直接 new 出来的实例，标记更可靠。
    /// </summary>
    private sealed class AvaloniaTokenDictionary : IResourceDictionary
    {
        private readonly IResourceDictionary _inner;

        public AvaloniaTokenDictionary(IResourceDictionary inner) => _inner = inner;

        public bool TryGetResource(object key, ThemeVariant? theme, out object? value)
            => _inner.TryGetResource(key, theme, out value);

        public IResourceProvider? Owner => _inner.Owner;

        public void AddResource(object key, object? value) => _inner.AddResource(key, value);

        public void RemoveResource(object key) => _inner.RemoveResource(key);

        public void Clear() => _inner.Clear();

        public void Changed() => _inner.Changed();

        public void Add(IResourceProvider provider) => _inner.Add(provider);

        public void Remove(IResourceProvider provider) => _inner.Remove(provider);

        public IEnumerable<IResourceProvider> Providers => _inner.Providers;
    }
}

/// <summary>
/// 系统深浅色探测（Avalonia 侧）。
///
/// WPF 侧读 Windows 注册表；Avalonia 侧用 Avalonia 自带的
/// <see cref="PlatformSettings"/>（macOS 读 AppleInterfaceStyle，Linux 读
/// GTK/Qt 的主题设置），无平台时返回 null（表示「探测不到」）。
/// </summary>
internal static class SystemThemeProbe
{
    /// <summary>
    /// 把静态探测器适配成 Shell 编排层要的实例接口
    /// （静态类不能实现接口 —— CS0714；而 Shell 层要一个可替换的实例，
    /// 测试才能塞个假探测器）。
    /// </summary>
    public static UMango.Shell.Theming.ISystemThemeProbe AsInterface { get; } = new ProbeAdapter();

    private sealed class ProbeAdapter : UMango.Shell.Theming.ISystemThemeProbe
    {
        public UMango.Core.UI.Theme.SystemThemeState Detect()
        {
            var dark = DetectNullable();
            return dark is null
                ? UMango.Core.UI.Theme.SystemThemeState.Unknown
                : UMango.Core.UI.Theme.SystemThemeState.Known(dark.Value);
        }
    }

    /// <summary>探测系统是否为深色；探测不到返回 null。</summary>
    public static bool? DetectNullable()
    {
        try
        {
            var settings = AvaloniaLocator.Current.GetService<PlatformSettings>();
            var variant = settings?.GetColorValues();
            if (variant is null)
            {
                return null;
            }

            // Avalonia 没有直接给「深色」标志，用窗口底色亮度判断：
            // 主题底色明显暗于 0.5 → 深色。
            var luminance = variant.ThemeBackgroundColor.R / 255.0 * 0.2126
                            + variant.ThemeBackgroundColor.G / 255.0 * 0.7152
                            + variant.ThemeBackgroundColor.B / 255.0 * 0.0722;
            return luminance < 0.5;
        }
        catch
        {
            // 探测失败不是致命问题 —— 调用方会沿用当前深浅色。
            return null;
        }
    }
}
