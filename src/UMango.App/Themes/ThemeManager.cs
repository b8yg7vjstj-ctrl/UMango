// 显式写 using System：虽然工程开了 ImplicitUsings，但主题切换器是核心基础件，
// 显式引用避免以后被别的工程（未开 ImplicitUsings）直接链接时编译不过。
using System;
using System.Windows;
using UMango.Core.Animation;

namespace UMango.App.Themes;

/// <summary>
/// 可选主题族。深浅色是同一族的第二个维度（见 <see cref="ThemeManager.Apply"/>）。
/// </summary>
public enum ThemeKind
{
    /// <summary>PCL 经典：1:1 复刻 PCL-CE 的观感（数值有录屏逐像素校验依据，不得改动）。</summary>
    PclClassic,

    /// <summary>Windows 10 Fluent：还原 Win10「设置」的观感。注意 Fluent Design ≠ WinUI3/Win11。</summary>
    Fluent10,
}

/// <summary>
/// 主题运行时切换器。
///
/// 为什么要有它：WPF 里 <c>{StaticResource}</c> 在加载时只求值一次，运行时换主题不会生效；
/// 因此本项目的约定是「token 集中放四套字典 + 界面一律用 <c>{DynamicResource}</c>」，
/// 由本类在运行时把旧的 token 字典整块换掉，界面元素不需要重建即可变色。
///
/// 用法：
/// <code>
/// ThemeManager.Apply(ThemeKind.Fluent10, dark: true);   // 切到 Win10 Fluent 深色
/// ThemeManager.ToggleDark();                             // 在当前主题族上切深浅
/// ThemeManager.ThemeChanged += () => { /* 自绘内容/图表需要重画时用 */ };
/// </code>
/// </summary>
public static class ThemeManager
{
    // ---- 四套字典在 Themes 目录内的相对路径（App.xaml 里也是这么写的）----
    public const string LightPclPath = "Themes/Tokens.Light.Pcl.xaml";
    public const string DarkPclPath = "Themes/Tokens.Dark.Pcl.xaml";
    public const string LightFluent10Path = "Themes/Tokens.Light.Fluent10.xaml";
    public const string DarkFluent10Path = "Themes/Tokens.Dark.Fluent10.xaml";

    /// <summary>
    /// 会被本类视为「主题 token 字典」并整体替换掉的文件名。
    /// 含历史文件 <c>PclTokens.xaml</c>：它现在只是「仅作参考/兼容」的壳，内部合并了 Tokens.Light.Pcl.xaml
    /// （App.xaml 里仍引用着它，故必须一并识别）。移除它的意义是保持 MergedDictionaries 干净：
    /// 否则切到深色后，旧浅色字典里那些"新主题没有的 key"还会被查找得到；
    /// 至于两套都有的 key，因为新字典是追加在末尾（优先级最高），不会出现浅色值遮住深色值的情况。
    /// </summary>
    private static readonly string[] TokenDictionaryFileNames =
    {
        "PclTokens.xaml",
        "Tokens.Light.Pcl.xaml",
        "Tokens.Dark.Pcl.xaml",
        "Tokens.Light.Fluent10.xaml",
        "Tokens.Dark.Fluent10.xaml",
    };

    /// <summary>当前主题族，默认 PCL 经典。</summary>
    public static ThemeKind CurrentKind { get; private set; } = ThemeKind.PclClassic;

    /// <summary>当前是否为深色。默认浅色（与改造前的界面一致）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// 主题已经换过之后触发；自绘内容（或需要按主题重算的布局）可以挂这里。
    /// 触发时机在资源字典替换完成之后，且保证在 UI 线程上（<see cref="Apply"/> 会做线程编组）。
    /// </summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// 当前主题族。DESIGN_UI_DOTNET.md §2.2 把该成员写作 <c>Current</c>，
    /// 而任务书要求 <c>CurrentKind</c>；两个名字都提供，避免并行实现的页面互相编译不过。
    /// </summary>
    public static ThemeKind Current => CurrentKind;

    /// <summary>当前生效的 token 字典在 Themes 目录内的相对路径（诊断/设置页回显用）。</summary>
    public static string CurrentDictionaryPath => ResolveRelativePath(CurrentKind, IsDark);

    /// <summary>
    /// 按主题族 + 深浅解析出对应的 token 字典相对路径。
    /// </summary>
    public static string ResolveRelativePath(ThemeKind kind, bool dark) => (kind, dark) switch
    {
        (ThemeKind.PclClassic, false) => LightPclPath,
        (ThemeKind.PclClassic, true) => DarkPclPath,
        (ThemeKind.Fluent10, false) => LightFluent10Path,
        _ => DarkFluent10Path,
    };

    /// <summary>
    /// 按主题族 + 深浅解析出可直接赋给 <see cref="ResourceDictionary.Source"/> 的绝对 pack URI。
    /// 这里用绝对 pack URI（而不是相对 URI）：相对 URI 需要有一个 base URI 才能解析，
    /// 而代码里 new 出来的 ResourceDictionary 不在任何可视化树上、没有 base URI，
    /// 用绝对 pack URI 才不会踩 "base URI 为 null" 的坑。
    /// 程序集名从 <see cref="Type.Assembly"/> 动态取，不写死字符串。
    /// </summary>
    public static Uri ResolveUri(ThemeKind kind, bool dark)
        => BuildPackUri(ResolveRelativePath(kind, dark));

    /// <summary>把 Themes 目录内的相对路径拼成 "pack://application:,,,/{程序集};component/{路径}"。</summary>
    private static Uri BuildPackUri(string relativePath)
    {
        // 主题字典属于 App 工程程序集（AssemblyName = UMango），用自身程序集名拼 pack URI，
        // 因此改 AssemblyName 也不会失效。
        var assemblyName = typeof(ThemeManager).Assembly.GetName().Name ?? "UMango";
        return new Uri($"pack://application:,,,/{assemblyName};component/{relativePath}", UriKind.Absolute);
    }

    /// <summary>
    /// 切换主题：从 <c>Application.Current.Resources.MergedDictionaries</c> 里移除旧的 token 字典，
    /// 再插入新的一套。Styles.xaml 与页面 XAML 都不需要动（它们只引用 DynamicResource）。
    /// </summary>
    public static void Apply(ThemeKind kind, bool dark)
    {
        var app = Application.Current;

        // 设计时（XAML 设计器）或单元测试里可能没有 Application：
        // 此时只记录状态、不抛异常，否则设计器一打开窗口就会崩。
        if (app is null)
        {
            CurrentKind = kind;
            IsDark = dark;
            ThemeChanged?.Invoke();
            return;
        }

        // 资源字典只能在 UI 线程上换，跨线程直接改会抛 InvalidOperationException。
        // 这里做一次编组：耗时很短（不涉及任何 CLI 调用），不会阻塞 UI。
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(kind, dark));
            return;
        }

        // 先构造新字典：Source 指向 Tokens.*.xaml，其内部就定义了完整的 key 集合。
        var themeDictionary = new ResourceDictionary
        {
            Source = ResolveUri(kind, dark),
        };

        var merged = app.Resources.MergedDictionaries;

        // 倒序移除，避免边遍历边删导致索引错位。
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (IsTokenDictionary(merged[i]))
            {
                merged.RemoveAt(i);
            }
        }

        // 追加到**末尾**：WPF 的资源查找在 MergedDictionaries 中从后往前找，
        // 后加入的字典优先级最高，因此即便有别的浅色字典漏在外面也不会遮住新主题。
        merged.Add(themeDictionary);

        CurrentKind = kind;
        IsDark = dark;

        // 动效预设随主题同步（需求：「动效之类的在 PCL 主题中完整保留」）。
        // 只写 profile 一个键 —— 读改写会覆盖用户单独改过的其他动画设置。
        Services.AppSettings.Settings.Set(
            UMango.Core.Configuration.UiSettings.AnimationProfileKey,
            kind == ThemeKind.PclClassic ? MotionProfileKind.Pcl : MotionProfileKind.Fluent);

        // 字典变更本身已让所有 {DynamicResource} 引用重新求值；
        // 该事件是给"需要主动重算"的调用方（自绘、图表配色）留的钩子。
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// 在当前主题族上切换浅色/深色（设置页的「深色模式」开关直接用这个）。
    /// </summary>
    public static void ToggleDark() => Apply(CurrentKind, !IsDark);

    /// <summary>
    /// 按跟随策略应用深浅色。
    ///
    /// 「跟随系统」时用的是 <see cref="SystemThemeProbe"/> 的探测结果；
    /// 探测不到（读不到注册表、没有 gsettings 等）就沿用当前深浅色，
    /// 不把界面切成随机状态。
    /// </summary>
    public static void ApplyFollowMode(UMango.Core.UI.Theme.ThemeFollowMode mode)
    {
        var system = SystemThemeProbe.Detect();
        var resolved = UMango.Core.UI.Theme.SystemThemeResolver.Resolve(mode, system, IsDark);
        Apply(CurrentKind, resolved);
    }

    /// <summary>
    /// 判断某个合并字典是不是主题 token 字典（按 Source 文件名识别，忽略路径）。
    /// 这里只认文件名而不是完整 URI，是为了容忍 ResourceDictionary 在不同位置被引用
    /// （例如 App.xaml 写 "Themes/X.xaml"、别的字典写 "X.xaml"）。
    /// </summary>
    private static bool IsTokenDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        if (string.IsNullOrEmpty(source))
        {
            return false;   // 无 Source 的字典是纯内联资源，不是主题字典
        }

        foreach (var fileName in TokenDictionaryFileNames)
        {
            if (source.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
