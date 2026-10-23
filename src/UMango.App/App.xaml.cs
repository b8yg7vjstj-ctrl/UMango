using System;
using System.Threading;
using System.Windows;
using UMango.App.Services;
using UMango.App.Themes;
using UMango.Core.App.Essentials;
using UMango.Core.Configuration;
using UMango.Core.Logging;

namespace UMango.App;

/// <summary>
/// UMango 应用入口。
///
/// 启动阶段只做两件事，且**都不允许把启动挡掉**：
///   1. <see cref="AppServices.Initialize"/>：预热组合根（包源注册表 / 记录存储 / 界面设置），
///      把首次访问的代价（构造 6 个适配器、读记录文件）挪到启动期，避免用户第一次切页时卡一下；
///   2. <see cref="ThemeManager.Apply"/>：把 App.xaml 里那套默认 token 换成"当前生效的主题"，
///      让主题字典在主窗口创建之前就位。
/// 其余一切（页面加载、CLI 探测）都在页面自己的 Loaded 里异步进行，不占用启动路径。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 动画驱动线程是否已启动（0=未启动，1=已启动）。
    /// 用 Interlocked 而不是 bool：OnStartup 理论上可能被重入，且要保证跨线程可见性。
    /// </summary>
    private static int _animationDriverStarted;

    /// <summary>
    /// 单实例判定结果（true=本进程是唯一实例）。OnStartup 里同步等待它。
    /// </summary>
    private static bool _isFirstInstance = true;

    /// <summary>
    /// 当前生效的灰度控制器。设置页改开关时用它立即施加，
    /// 否则光写设置项要等到下次启动或换主题才看得见效果。
    /// 为 <c>null</c> 表示在测试宿主里没有窗口与资源字典。
    /// </summary>
    public static Themes.GrayModeController? Gray => _grayInstance;

    /// <summary>
    /// 当前生效的资源字典。<c>null</c> 表示无 UI 宿主（单元测试）。
    /// 放在这里而不是让 ViewModel 碰 <c>Application.Current</c>：
    /// 那会让 ViewModel 直接引用 WPF 类型，在 macOS 测试宿主里加载 PresentationFramework 时崩。
    /// </summary>
    public static ResourceDictionary? GrayResources => Current?.Resources;

    /// <summary>
    /// 灰度（哀悼日）模式控制器。必须在 <see cref="ApplySavedTheme"/> **之后**创建：
    /// 它要改写的是「当前生效」那套 token 字典里的画笔，先换字典再脱色才对。
    /// </summary>
    private Themes.GrayModeController? _gray;

    /// <summary>同一实例的静态可达形式，供 ViewModel 立即施加灰度。</summary>
    private static Themes.GrayModeController? _grayInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        SeedServices();
        HookGlobalExceptionHandlers();
        ApplySavedTheme();
        ApplySavedLanguage();

        // 灰度：挂在 ApplySavedTheme 之后 —— 它改的是「当前生效」那套字典的画笔。
        _gray = _grayInstance = new Themes.GrayModeController();
        _gray.ApplyFromSettings(Current.Resources);
        ThemeManager.ThemeChanged += OnThemeChangedForGray;

        // 单实例：第二个实例通知首个实例激活主窗口后自行退出。
        // 思路来源：PCL-CE SingleInstanceService（Apache-2.0）—— 单例锁 + 命名管道传激活指令。
        // 必要性：用户重复双击 EXE（或启动器/快捷方式连点）会产生第二个进程，
        // 真机日志实测过 500ms 内两次「UMango 启动」（双窗口、双份日志、双套探测）。
        // 锁文件在退出时删除（见 SingleInstanceService.DisposeAsync），崩溃后不会残留误判。
        _isFirstInstance = await Services.SingleInstanceService.TryStartAsync(ActivateExistingWindowAsync)
            .ConfigureAwait(false);
        if (!_isFirstInstance)
        {
            Log.Flush();
            Shutdown();
            return;
        }

        // 放在最后：基类负责触发 Startup 事件，StartupUri 指向的主窗口在其之后创建，
        // 因此上面两步都先于首帧完成（主题不会"先用默认色画一帧再变"）。
        base.OnStartup(e);

        // 启动 PCL-CE 原版动画驱动线程（ModAnimation.AniStart() 自带线程循环 + FPS 限制，
        // 帧率上限经 PclCompat 桥接读取 UMango 的动画设置）。采用自 PCL-CE，见 Pcl/ 文件头。
        //
        // ⚠ 必须只调一次，因此这里加守卫：
        //   PCL 原版的 AniStart() **没有防重入**（`aniRunning` 字段只被写、从不被读 ——
        //   见 ModAnimation.cs:33/461）。调两次就会起两个驱动线程，各自独立推进
        //   AniTimer 的 delta，同一份动画增量被施加两次 → 动效速度翻倍并抖动。
        //   PCL 自己只在启动时调一次，所以原版不会暴露这个问题。
        //   这不是假想：真机日志里出现过两次"启动"（多个实例/重入），正是这条路径。
        // 修在调用点而不是改 PCL 文件 —— 保持该文件逐字采用（见 Pcl/ModAnimation.cs 文件头）。
        if (Interlocked.Exchange(ref _animationDriverStarted, 1) == 0)
        {
            PCL.ModAnimation.AniStart();
        }
    }

    /// <summary>
    /// 激活已在运行的主窗口（由第二个实例通过命名管道触发）。
    ///
    /// 做法与 PCL-CE 的 activate 处理一致：最小化则还原，
    /// 再用一次 Topmost 置真/置假把窗口"拍"到前台，最后 Activate。
    /// 之所以要置顶闪烁：仅 Activate() 在某些情况下不会让已最小化的窗口
    /// 抢到焦点（Windows 只允许前台进程设置前台窗口）。
    /// </summary>
    private Task ActivateExistingWindowAsync()
    {
        var window = MainWindow;
        if (window is null) return Task.CompletedTask;

        if (window.Dispatcher.CheckAccess())
        {
            ActivateWindow(window);
        }
        else
        {
            window.Dispatcher.Invoke(() => ActivateWindow(window));
        }

        return Task.CompletedTask;
    }

    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        if (!window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }

        window.Activate();
        Log.Info("SingleInstance", "已激活已在运行的主窗口");
    }

    /// <summary>
    /// 接管全局未处理异常，把它们写进日志。
    ///
    /// **这是本项目最需要的诊断能力**：应用主要在 Windows 上运行，而开发发生在 macOS ——
    /// 一旦真机崩溃，唯一的定位材料就是这份日志。
    /// 未接管的异常在 WPF 里表现为"窗口直接消失"，什么线索都不留。
    ///
    /// 注意：**不阻止**未处理异常（不设 e.Handled = true 去掩盖崩溃），
    /// 只是把它记录下来。掩盖会让应用带着坏状态继续跑，问题更难查。
    /// </summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.ErrorAt("App", args.Exception, member: "DispatcherUnhandledException");
            Log.Flush();   // 崩溃前必须刷盘，否则最后几行（最关键）会丢
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.ErrorAt("App", ex, member: "AppDomainUnhandled");
            else
                Log.Error("App", $"未处理的非异常错误：{args.ExceptionObject}");

            Log.Flush();
        };

        // 后台 Task 的异常不会冒到上面两个处理器，必须单独接：
        // 否则一个 fire-and-forget 的任务失败会静默消失，只表现为"某个功能没反应"。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.ErrorAt("App", args.Exception, member: "UnobservedTask");
            args.SetObserved();   // 这条只记录不上报（已记录，且无其它处理者）
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Info("App", $"UMango 退出（代码 {e.ApplicationExitCode}）");
            Log.Flush();
            AppServices.LogFile?.Close();

            // 释放单例锁：删掉锁文件，避免进程被强杀后残留的锁让后续启动
            // 永远误判为"已有实例"（那时用户将永远打不开第二个窗口）。
            Services.SingleInstanceService.DisposeAsync().AsTask().GetAwaiter().GetResult();

            // 还原灰度改写的画笔：进程虽然在退出，但画刷可能被静态引用持有，
            // 留着脱色值会在设计器/复用场景下造成"颜色莫名其妙一直是灰的"。
            if (_gray is not null)
            {
                _gray.Dispose();
                _gray = null;
            }
        }
        catch (Exception)
        {
            // 退出路径同样不能抛
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 预热组合根。方法内部已经逐项容错（构造失败会退化成"可用但为空"并把原因记进
    /// <see cref="AppServices.LastError"/>），这里再兜一层是因为它正好在启动路径上：
    /// 任何意外都不该表现为"应用打不开"，而应该表现为"某个列表是空的"。
    /// </summary>
    private static void SeedServices()
    {
        try
        {
            AppServices.Initialize();
        }
        catch (Exception)
        {
            // 静默继续：能走到这里的失败原因已经在 AppServices 内部记录（工具页可见）。
        }
    }

    /// <summary>
    /// 应用设置里保存的主题。
    ///
    /// 设置服务是主题选择的**持久化权威**（描述符见 Core 的 UiSettings），
    /// ThemeManager 是**运行时权威**（它负责替换 token 字典）。
    /// 启动时把前者读出来交给后者，就实现了"恢复上次的主题"。
    /// 没有保存过设置时读到的是描述符里的默认值 = PCL 经典 + 浅色，正是需求要求的缺省。
    /// </summary>
    /// <summary>
    /// 应用已保存的语言（在主题之后、主窗口创建之前）。
    ///
    /// 顺序很重要：资源字典必须在首帧之前就位，否则会"先用默认语言画一帧再跳"。
    /// 与 <see cref="ApplySavedTheme"/> 同理。
    /// </summary>
    private static void ApplySavedLanguage()
    {
        try
        {
            Localization.AppLocalization.ApplySavedLanguage();
        }
        catch (Exception ex)
        {
            // 语言加载失败绝不能挡住启动：界面退回默认语言（字典缺失时
            // DynamicResource 找不到键会显示空串，但应用仍可用）
            Log.ErrorAt("App", ex, member: "ApplySavedLanguage");
        }
    }

    /// <summary>主题深浅/主题族切换后，重算灰度（两套音色不同）。</summary>
    private void OnThemeChangedForGray()
        => _gray?.OnThemeChanged(Current.Resources);

    private static void ApplySavedTheme()
    {
        try
        {
            var family = AppSettings.Settings.Get<ThemeFamily>(UiSettings.ThemeKindKey);
            var dark = AppSettings.Settings.Get<bool>(UiSettings.DarkModeKey);

            var kind = family == ThemeFamily.Fluent10 ? ThemeKind.Fluent10 : ThemeKind.PclClassic;
            ThemeManager.Apply(kind, dark);
        }
        catch (Exception)
        {
            // 主题字典缺失/损坏时保持 App.xaml 里的默认（PCL 经典浅色）继续启动：
            // 观感不对远好于窗口打不开。
        }
    }
}

/// <summary>
/// 程序入口与更新助手进程分支。
///
/// **为什么独立成类而不是放在 <see cref="App"/> 里**：WPF 的 ApplicationDefinition
///（App.xaml）会自动生成一个 <c>App.Main()</c>，若我们再在同一个 partial 类里写一个
/// <c>Main</c>，编译器无法判断该用哪个（<c>/main:UMango.App.App</c> 只指定类型，
/// 不区分同一类型里的多个入口点）→ <c>CS0017 程序定义了多个入口点</c>。
/// 放到单独的类型里，再用 <c>&lt;StartupObject&gt;</c> 指向它，就能明确选定我们自己的版本。
/// </summary>
internal static class Program
{
/// <summary>
/// 程序入口。
///
/// **为什么不用 WPF 自动生成的 Main**：自动生成的版本只会 new App + Run，
/// 没有任何机会在创建 Application（拖入全部 WPF 资源）之前检查参数。
/// 自更新助手进程（<c>update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;restart&gt;</c>）
/// 必须**不启动主界面、不初始化主题、不抢单实例锁** —— 它只是个替换文件的小工具，
/// 跑完就该退位。放在 Main 里分支是唯一能让它完全绕开 WPF 启动路径的位置。
///
/// 协议见 <see cref="UpdateCommandLine"/>（Core 层，已被单测覆盖）。
/// </summary>
[STAThread]
public static void Main(string[] args)
{
    if (UpdateCommandLine.IsUpdateCommand(args))
    {
        RunUpdateHelper(args);
        return;
    }

    var app = new App();
    app.InitializeComponent();
    app.Run();
}

/// <summary>
/// 更新助手进程的三种模式：
/// ① <c>update …</c>：等旧进程退出 → 替换文件 → 重启自己（带 update_finished）→ 本进程退位；
/// ② <c>update_finished &lt;source&gt;</c>：删掉下载来的安装包，然后**继续正常启动**
///    （新版本已就位，用户此刻等的是主界面打开，而不是又一个空窗口都不开的进程）；
/// ③ <c>update_failed &lt;reason&gt;</c>：只记日志，同样继续启动 ——
///    此时旧文件已被 UpdateHelper 还原，程序至少还能开，比闪退好。
/// </summary>
private static void RunUpdateHelper(string[] args)
{
    // 助手也要留痕：更新失败是最难远程复现的问题。
    // 直接走 Log 而不是 AppServices.InitializeLogging()（那是 private，
    // 且会把 sink 建到正式日志目录里）—— Log 在无 sink 时有惰性兜底。
    AppServices.InitializeLoggingForHelper();

    switch (UpdateCommandLine.Parse(args))
    {
        case UpdateCommandLine.ApplyRequest apply:
            UpdateCommandLine.RunApply(apply, Environment.ProcessPath);
            Log.Flush();
            return;

        case UpdateCommandLine.FinishedRequest finished:
            var deleteError = UpdateCommandLine.RunFinished(finished);
            if (deleteError is not null)
            {
                // 删不掉安装包只是留了个垃圾文件，不影响新版本可用性，别升级成弹窗
                Log.Warn("Update", $"清理安装包失败：{deleteError.Message}");
            }
            break;

        case UpdateCommandLine.FailedRequest failed:
            Log.Error("Update", $"更新失败：{failed.Reason}");
            break;

        default:
            Log.Error("Update", $"更新参数无法解析：{string.Join(' ', args)}");
            return;
    }

    Log.Flush();

    // ②/③ 都要接着启动主界面
    var app = new App();
    app.InitializeComponent();
    app.Run();
}
}
