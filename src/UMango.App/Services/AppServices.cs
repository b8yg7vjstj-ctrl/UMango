using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using UMango.Core.Install;
using UMango.Core.Animation;
using UMango.Core.Caching;
using UMango.Core.Configuration;
using UMango.Core.Download;
using UMango.Core.Logging;
using UMango.Core.Notifications;
using UMango.Core.Platform;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using UMango.Core.Terminal;

namespace UMango.App.Services;

/// <summary>
/// 组合根（composition root）：全进程唯一的服务入口。
///
/// 为什么要有它：
///   · 包源注册表、记录存储、安装执行器必须**全进程只有一份**——
///     注册表里有可用性探测缓存，记录存储里有内存流水，执行器要往同一个存储里写记录；
///     如果每个页面各自 new 一套，就会出现"主页显示 3 次记录、设置页显示 0 次"这类不一致。
///   · 页面/ViewModel 只通过静态属性拿服务，不需要依赖注入容器，
///     也避免把服务的创建顺序问题（谁先 new）泄漏给每一个页面。
///
/// 三条硬性约定：
///   1. **懒加载**：属性第一次被访问时才构造，启动阶段不做任何 CLI 探测；
///   2. **构造失败不上抛**：记录文件损坏、某平台探测逻辑异常都不该让界面起不来，
///      失败时退化成"可用但为空"的状态并把原因记在 <see cref="LastError"/>（工具页可见）；
///   3. <see cref="Initialize"/> 幂等，供 App.xaml.cs 在启动时主动预热。
/// </summary>
public static class AppServices
{
    // Lazy<T> 默认线程安全（ExecutionAndPublication）：多个页面同时首次访问也只会构造一次。
    private static readonly Lazy<PackageManagerRegistry> LazyRegistry = new(CreateRegistry);
    private static readonly Lazy<InstallRecordStore> LazyRecords = new(CreateRecords);
    private static readonly Lazy<InstallExecutor> LazyExecutor = new(() => new InstallExecutor(Registry, Records));

    /// <summary>包源注册表（已注册全部 6 个适配器，平台门控由注册表负责）。</summary>
    public static PackageManagerRegistry Registry => LazyRegistry.Value;

    /// <summary>管理记录与次数记录存储（构造后已尝试从磁盘加载）。</summary>
    public static InstallRecordStore Records => LazyRecords.Value;

    /// <summary>安装执行器（复用上面两个单例）。</summary>
    public static InstallExecutor Executor => LazyExecutor.Value;

    /// <summary>
    /// 全局通知服务（提示队列）。
    /// PCL 的 <c>HintWrapper</c> 经 <see cref="PclUiBridge"/> 汇入这里；
    /// 界面上的消费方订阅 <see cref="UMango.Core.Notifications.NotificationService.OnShow"/> 即可。
    /// </summary>
    public static NotificationService Notifications { get; } = new();

    /// <summary>
    /// 内存日志（界面"日志"页直接读它，不必解析文件）。
    /// 由 <see cref="Initialize"/> 初始化；未初始化时为 null。
    /// </summary>
    public static MemoryLogSink? Logs { get; private set; }

    /// <summary>文件日志（"导出日志"与"打开日志目录"用）。未初始化时为 null。</summary>
    public static FileLogSink? LogFile { get; private set; }

    /// <summary>
    /// 日志目录（连同设置与记录文件一起放在用户配置目录下，便于一次性收集）。
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        Path.GetDirectoryName(InstallRecordStore.DefaultStorePath) ?? ".",
        "logs");

    /// <summary>
    /// 下载引擎注册表（aria2 / eD2k / 内置 HttpClient）。
    /// 独立于包管理器适配器：下载器是通用能力，任何链接都能下，不只是安装包。
    /// </summary>
    public static DownloadEngineRegistry DownloadEngines => LazyEngines.Value;

    private static readonly Lazy<DownloadEngineRegistry> LazyEngines =
        new(() => new DownloadEngineRegistry(), isThreadSafe: true);

    /// <summary>
    /// 初始化过程中被吞掉的错误（正常为 null）。
    /// 之所以要暴露出来：错误被 try/catch 吞掉后如果完全没有痕迹，
    /// 排查"为什么包源列表是空的"就只能靠猜；工具页把它显示出来即可定界。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>记录一次初始化失败（同时写日志 —— LastError 重启就没了，日志不会）。</summary>
    internal static void ReportInitError(string what, Exception ex)
    {
        LastError = $"{what}：{ex.Message}";
        Log.Error("Startup", ex, what);
    }

    /// <summary>
    /// 预热服务并恢复界面设置。**幂等**，可在启动时无脑调用。
    /// 主动摸一次懒加载属性，把首次访问的代价（构造适配器、读记录文件）挪到启动阶段，
    /// 避免用户第一次切页时卡一下。
    /// </summary>
    /// <summary>
    /// 初始化日志。
    ///
    /// **必须在其它初始化之前调用** —— 否则前面那些步骤出的问题不会留下任何痕迹，
    /// 而这正是最需要日志的地方（"启动就崩"往往发生在组合根构造期间）。
    /// </summary>
    private static void InitializeLogging()
    {
        try
        {
            var memory = new MemoryLogSink(capacity: 3000);
            var file = new FileLogSink(LogDirectory);
            Log.Initialize(memory, file);

            Logs = memory;
            LogFile = file;

            Log.Info("App", $"UMango 启动；日志目录 {LogDirectory}");
            Log.Info("App", SystemInfo.Describe());
        }
        catch (Exception)
        {
            // 日志系统起不来也必须能继续启动 —— 宁可不记日志，也不能打不开应用
        }
    }

    public static void Initialize()
    {
        InitializeLogging();

        _ = Registry;
        _ = Records;
        _ = Executor;
        AppSettings.Load();

        // PCL UI 契约接线：采用 PCL 代码后，HintWrapper/MsgBoxWrapper 的调用方
        // （PCL 侧代码）通过事件把提示与对话框交给界面层，这里接上 UMango 的实现。
        PclUiBridge.Wire(Notifications);
    }

    /// <summary>
    /// 包源可用性缓存的持久化路径。
    /// 与设置/记录同目录，便于一起备份，也便于"清空配置"时一并清掉。
    /// </summary>
    public static string AvailabilityCachePath { get; } = Path.Combine(
        Path.GetDirectoryName(InstallRecordStore.DefaultStorePath) ?? ".",
        "source-availability.json");

    private static PackageManagerRegistry CreateRegistry()
    {
        try
        {
            // 传入持久化缓存：没有它的话，每次启动都要重新探测全部六个包源
            // （最多 12 次进程启动），而可用性一天内几乎不变。
            return PackageManagerFactory.CreateDefault(new JsonAvailabilityCache(AvailabilityCachePath));
        }
        catch (Exception ex)
        {
            // 某个适配器构造抛异常（例如 Windows 专有路径解析）时不让应用起不来：
            // 退化成"一个包源都没有"的空注册表，界面显示空分类列表而不是直接崩溃。
            ReportInitError("包源注册表创建失败", ex);
            return new PackageManagerRegistry(Array.Empty<IPackageManager>());
        }
    }

    private static InstallRecordStore CreateRecords()
    {
        InstallRecordStore store;
        try
        {
            store = new InstallRecordStore();
        }
        catch (Exception ex)
        {
            // 连存储对象都建不起来时退到临时目录：历史记录可以丢，界面不能不显示。
            ReportInitError("记录存储创建失败", ex);
            return new InstallRecordStore(Path.Combine(Path.GetTempPath(), "umango-records.json"));
        }

        try
        {
            // InstallRecordStore.Load 内部已经容错（文件损坏时保留空集合），
            // 这里再加一层是因为"记录文件被别的程序占着/权限不足"也可能抛出来，
            // 而它发生在 App 启动路径上，绝不能把启动挡掉。
            store.Load();
        }
        catch (Exception ex)
        {
            ReportInitError("管理记录加载失败（已按空记录继续）", ex);
        }

        return store;
    }
}

/// <summary>
/// 界面设置的入口。
///
/// **唯一权威是 Core 的 <see cref="SettingsService"/>**：
/// 这里不再手写 JSON 节点，而是把设置声明交给 <see cref="UiSettings"/> 的描述符目录，
/// 于是"新增一个设置"只需要加一条描述符，不用在这里补一对手写读写。
///
/// 历史说明：本类原先自己拼 JsonObject 存 debugMode，后来加了动画设置又要各写一遍，
/// 属于典型的重复代码；且默认值会散落两边（设置页显示一个、动画系统用另一个）。
/// </summary>
public static class AppSettings
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>设置文件路径（与 install-records.json 同目录，便于一起备份/清理）。</summary>
    public static string SettingsPath { get; } = Path.Combine(
        Path.GetDirectoryName(InstallRecordStore.DefaultStorePath) ?? ".",
        "settings.json");

    /// <summary>设置服务（描述符已注册）。</summary>
    public static SettingsService Settings { get; } = CreateService();

    private static SettingsService CreateService()
    {
        var service = new SettingsService(new JsonFileSettingsStore(SettingsPath));
        UiSettings.RegisterAll(service);

        // DebugMode 这个键要同步到 Core 的静态开关（TerminalBridge.DebugMode）——
        // 那是终端桥的实际读取点，不能在两处各存一份。
        service.Changed += key =>
        {
            if (key == UiSettings.DebugModeKey)
            {
                TerminalBridge.DebugMode = service.Get<bool>(UiSettings.DebugModeKey);
            }
        };
        return service;
    }

    /// <summary>调试模式（读写经设置服务，并同步到 TerminalBridge 的静态开关）。</summary>
    public static bool DebugMode
    {
        get { EnsureLoaded(); return Settings.Get<bool>(UiSettings.DebugModeKey); }
        set
        {
            EnsureLoaded();
            if (Settings.Get<bool>(UiSettings.DebugModeKey) == value) return;
            Settings.Set(UiSettings.DebugModeKey, value);
            Save();
        }
    }

    /// <summary>
    /// 当前的动画设置（每次读取都从设置服务重新构造）。
    /// 传值给 AnimationPlanner 用，**不要长期缓存这个对象** —— 见 UiSettings 的说明。
    /// </summary>
    public static AnimationSettings Animation
    {
        get { EnsureLoaded(); return UiSettings.ReadAnimationSettings(Settings); }
    }

    /// <summary>从磁盘恢复设置。幂等，供 <see cref="AppServices.Initialize"/> 调用。</summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Settings.Load();
                // 载入后把静态开关同步一次（否则上次的调试模式要等用户再点一下才生效）
                TerminalBridge.DebugMode = Settings.Get<bool>(UiSettings.DebugModeKey);
            }
            catch (Exception)
            {
                // 设置文件损坏 / 无权限：保持默认值继续。
                // 一份读不出来的设置文件不应该拦住整个应用的启动。
            }
        }
    }

    /// <summary>懒恢复：不依赖调用方是否记得先调 Load。</summary>
    private static void EnsureLoaded()
    {
        if (_initialized) return;
        Load();
    }

    /// <summary>落盘。任何一次设置变更后调用。</summary>
    public static void Save()
    {
        try { Settings.Save(); }
        catch (Exception)
        {
            // 落盘失败不应中断业务：内存里的设置仍然有效，下次变更会再试
        }
    }

    /// <summary>诊断/备份用：导出当前设置 JSON。</summary>
    public static string ExportJson() => Settings.ToJson();
}

/// <summary>
/// 把动作切回 UI 线程。
///
/// 为什么必须有：Core 的终端桥/下载会话在线程池线程（CLI 的 stdout/stderr 回调）上触发事件，
/// 而 WPF 的 ObservableCollection 只能在拥有它的 UI 线程上修改，否则绑定会抛
/// "This type of CollectionView does not support changes from a different thread"。
/// 因此凡是"订阅 Core 事件 → 改界面集合"的地方，都要经过这里。
///
/// **★ 为什么用反射而不是直接写 System.Windows.Application.Current ★**
/// 直接写会在**编译期**产生对 PresentationFramework/WindowsBase 的类型引用。
/// 后果是：在没有 WPF 运行时的宿主里（macOS 上跑单元测试、设计器），
/// 一旦这个方法被 JIT，就会抛 FileNotFoundException 而**方法体内的 try/catch 根本来不及生效** ——
/// JIT 失败发生在方法能执行之前。实测就是这样崩的：
///   Could not load file or assembly 'WindowsBase, Version=10.0.0.0'
///     at UMango.App.Services.UiDispatch.Post(...)
/// 改用反射后不存在编译期引用，取不到就退化为"直接执行"，
/// 于是这个助手在任何宿主下都是安全的，App 层的 ViewModel 也就能在 macOS 上被单测覆盖。
/// </summary>
internal static class UiDispatch
{
    /// <summary>是否已确认过取不到 WPF 调度器（取不到就不再反复反射）。</summary>
    private static bool _wpfUnavailable;

    /// <summary>已在 UI 线程则直接执行，否则投递到 Dispatcher 异步执行（调用方不等待）。</summary>
    public static void Post(Action action)
    {
        var dispatcher = TryGetDispatcher();

        // 没有 WPF 应用上下文（无头测试、设计器、macOS 开发机）时直接执行：
        // 此时不存在"界面集合的线程亲和性"约束，直接跑是安全的。
        if (dispatcher is null)
        {
            action();
            return;
        }

        try
        {
            // Dispatcher.CheckAccess / InvokeAsync 也走反射，避免引入类型引用
            var checkAccess = dispatcher.GetType().GetMethod("CheckAccess");
            if (checkAccess?.Invoke(dispatcher, null) is true)
            {
                action();
                return;
            }

            var invokeAsync = dispatcher.GetType().GetMethod("InvokeAsync", [typeof(Action)]);
            if (invokeAsync is not null)
            {
                invokeAsync.Invoke(dispatcher, [action]);
                return;
            }

            action();   // 拿不到 InvokeAsync：退化为直接执行，至少不丢事件
        }
        catch (Exception)
        {
            // Dispatcher 已关闭（应用退出中）时投递必然失败：退化为直接执行，
            // 保证最后一个任务的收尾输出不会因为窗口正在关闭而丢掉。
            action();
        }
    }

    /// <summary>
    /// 反射取 WPF 的当前 Dispatcher；宿主没有 WPF 时返回 null。
    /// 一旦确认取不到就缓存结论，避免每次回调都做一轮反射。
    /// </summary>
    private static object? TryGetDispatcher()
    {
        if (_wpfUnavailable) return null;

        try
        {
            var appType = Type.GetType("System.Windows.Application, PresentationFramework");
            if (appType is null) { _wpfUnavailable = true; return null; }

            var current = appType.GetProperty("Current", System.Reflection.BindingFlags.Public
                                                              | System.Reflection.BindingFlags.Static);
            var app = current?.GetValue(null);
            if (app is null) return null;

            return app.GetType().GetProperty("Dispatcher")?.GetValue(app);
        }
        catch (Exception)
        {
            // 宿主缺 WindowsBase/PresentationFramework：记下结论，后续直接走"直接执行"路径
            _wpfUnavailable = true;
            return null;
        }
    }
}
