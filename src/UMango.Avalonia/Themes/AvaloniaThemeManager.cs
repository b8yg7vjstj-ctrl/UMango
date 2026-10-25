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

/// <summary>可选主题族。深浅色是同一族的第二个维度。</summary>
public enum ThemeKind
{
    /// <summary>PCL 经典：1:1 复刻 PCL-CE 的观感（数值有录屏逐像素校验依据，不得改动）。</summary>
    PclClassic,

    /// <summary>Windows 10 Fluent：还原 Win10「设置」的观感。注意 Fluent Design ≠ WinUI3/Win11。</summary>
    Fluent10,
}

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

    /// <summary>当前主题族，默认 PCL 经典。</summary>
    public static ThemeKind CurrentKind { get; private set; } = ThemeKind.PclClassic;

    /// <summary>当前是否为深色。默认浅色（与 WPF 侧一致）。</summary>
    public static bool IsDark { get; private set; }

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
        var app = Application.Current;

        // 设计时 / 单元测试里可能没有 Application：只记录状态，不抛异常。
        if (app is null)
        {
            CurrentKind = kind;
            IsDark = dark;
            ThemeChanged?.Invoke();
            return;
        }

        // 资源字典只能在 UI 线程上换。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Apply(kind, dark));
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

        CurrentKind = kind;
        IsDark = dark;

        // 动效预设随主题同步（与 WPF 侧行为一致）。
        // 只写 profile 一个键 —— 读改写会覆盖用户单独改过的其他动画设置。
        AppSettings.Settings.Set(
            UiSettings.AnimationProfileKey,
            kind == ThemeKind.PclClassic ? MotionProfileKind.Pcl : MotionProfileKind.Fluent);

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
        var system = SystemThemeProbe.Detect();
        var resolved = UMango.Core.UI.Theme.SystemThemeResolver.Resolve(mode, system, IsDark);
        Apply(CurrentKind, resolved);
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
    public static bool? Detect()
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
