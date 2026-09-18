using System.Windows;
using UMango.App.Services;
using UMango.App.Themes;

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
        ApplySavedTheme();

        // 放在最后：基类负责触发 Startup 事件，StartupUri 指向的主窗口在其之后创建，
        // 因此上面两步都先于首帧完成（主题不会"先用默认色画一帧再变"）。
        base.OnStartup(e);
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
    /// 应用"设置里保存的主题"。
    ///
    /// 为什么读的是 <see cref="ThemeManager"/> 而不是自己去翻设置文件：
    /// 主题的唯一权威就是 ThemeManager，设置页切换主题时也是调它（不另存一份），
    /// 因此在启动时把它的当前状态重新 Apply 一遍，等价于"恢复上次的主题"；
    /// 而**没有保存过设置时它的默认值正是 PCL 经典 + 浅色**（需求要求的缺省）。
    /// 这样将来设置页把选择持久化进 ThemeManager 之后，这一行不需要任何改动。
    /// </summary>
    private static void ApplySavedTheme()
    {
        try
        {
            ThemeManager.Apply(ThemeManager.CurrentKind, ThemeManager.IsDark);
        }
        catch (Exception)
        {
            // 主题字典缺失/损坏时保持 App.xaml 里的默认（PCL 经典浅色）继续启动：
            // 观感不对远好于窗口打不开。
        }
    }
}
