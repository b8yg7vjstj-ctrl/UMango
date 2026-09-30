using System.Windows;
using UMango.App.Services;
using UMango.App.Themes;
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
    protected override void OnStartup(StartupEventArgs e)
    {
        SeedServices();
        HookGlobalExceptionHandlers();
        ApplySavedTheme();

        // 放在最后：基类负责触发 Startup 事件，StartupUri 指向的主窗口在其之后创建，
        // 因此上面两步都先于首帧完成（主题不会"先用默认色画一帧再变"）。
        base.OnStartup(e);

        // 启动 PCL-CE 原版动画驱动线程（ModAnimation.AniStart() 自带线程循环 + FPS 限制，
        // 帧率上限经 PclCompat 桥接读取 UMango 的动画设置）。采用自 PCL-CE，见 Pcl/ 文件头。
        PCL.ModAnimation.AniStart();
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
