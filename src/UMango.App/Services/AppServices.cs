using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using UMango.Core.Install;
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
    /// 初始化过程中被吞掉的错误（正常为 null）。
    /// 之所以要暴露出来：错误被 try/catch 吞掉后如果完全没有痕迹，
    /// 排查"为什么包源列表是空的"就只能靠猜；工具页把它显示出来即可定界。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 预热服务并恢复界面设置。**幂等**，可在启动时无脑调用。
    /// 主动摸一次懒加载属性，把首次访问的代价（构造适配器、读记录文件）挪到启动阶段，
    /// 避免用户第一次切页时卡一下。
    /// </summary>
    public static void Initialize()
    {
        _ = Registry;
        _ = Records;
        _ = Executor;
        AppSettings.Load();
    }

    private static PackageManagerRegistry CreateRegistry()
    {
        try
        {
            return PackageManagerFactory.CreateDefault();
        }
        catch (Exception ex)
        {
            // 某个适配器构造抛异常（例如 Windows 专有路径解析）时不让应用起不来：
            // 退化成"一个包源都没有"的空注册表，界面显示空分类列表而不是直接崩溃。
            LastError = $"包源注册表创建失败：{ex.Message}";
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
            LastError = $"记录存储创建失败：{ex.Message}";
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
            LastError = $"管理记录加载失败（已按空记录继续）：{ex.Message}";
        }

        return store;
    }
}

/// <summary>
/// 界面设置的极简持久化（目前只有「调试模式」）。
///
/// 为什么需要：Core 的 <see cref="TerminalBridge.DebugMode"/> 是**静态**开关，
/// 进程重启就没了，而用户显然期望"上次打开过调试模式，下次还是打开的"。
/// 存储位置与 install-records.json 同目录，便于一起备份/清理。
/// </summary>
public static class AppSettings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static bool _loaded;

    /// <summary>设置文件路径（与记录文件同目录）。</summary>
    public static string SettingsPath { get; } = Path.Combine(
        Path.GetDirectoryName(InstallRecordStore.DefaultStorePath) ?? ".",
        "ui-settings.json");

    /// <summary>
    /// 调试模式。读写的是 Core 的静态开关本身（唯一权威），
    /// 避免"内存镜像"和真实开关不一致；写入时顺手落盘。
    /// </summary>
    public static bool DebugMode
    {
        get
        {
            EnsureLoaded();   // 读取前先保证已尝试恢复，见 EnsureLoaded 的说明
            return TerminalBridge.DebugMode;
        }
        set
        {
            if (TerminalBridge.DebugMode == value) return;
            TerminalBridge.DebugMode = value;
            Save();
        }
    }

    /// <summary>从磁盘恢复设置。幂等，供 <see cref="AppServices.Initialize"/> 调用。</summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(SettingsPath)) return;
                if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is JsonObject root
                    && root["debugMode"] is JsonValue value
                    && value.TryGetValue<bool>(out var flag))
                {
                    TerminalBridge.DebugMode = flag;
                }
            }
            catch (Exception)
            {
                // 设置文件损坏 / 无权限：保持默认值继续。
                // 一个界面开关读不出来，不应该拦住整个应用的启动。
            }
        }
    }

    /// <summary>
    /// 懒恢复：不依赖调用方是否记得先调 <see cref="AppServices.Initialize"/>。
    /// （App.xaml.cs 由外壳 agent 维护，这里不能假设它的调用时机。）
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        Load();
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var node = new JsonObject { ["debugMode"] = TerminalBridge.DebugMode };
            File.WriteAllText(SettingsPath, node.ToJsonString(JsonOpts));
        }
        catch (Exception)
        {
            // 落盘失败不影响本次会话：开关已经在内存/TerminalBridge 上生效了。
        }
    }
}

/// <summary>
/// 把回调切回 UI 线程。
///
/// 为什么必须有：Core 的终端桥在线程池线程（CLI 的 stdout/stderr 回调）上触发
/// <see cref="TerminalBridge.LineAppended"/>，而 WPF 的 ObservableCollection 只能在
/// 拥有它的 UI 线程上修改，否则绑定会抛
/// "This type of CollectionView does not support changes from a different thread"。
/// 因此凡是"订阅 Core 事件 → 改界面集合"的地方，都要经过这里。
/// </summary>
internal static class UiDispatch
{
    /// <summary>已在 UI 线程则直接执行，否则投递到 Dispatcher 异步执行（调用方不等待）。</summary>
    public static void Post(Action action)
    {
        Dispatcher? dispatcher = null;
        try
        {
            dispatcher = Application.Current?.Dispatcher;
        }
        catch (Exception)
        {
            // 宿主没有 WPF（无头测试、设计器）时 Application.Current 可能直接抛（缺 WindowsBase 等）。
            // 这里必须吞掉：本方法处在 Core 事件回调的调用栈上（CLI 的输出线程），
            // 抛回去会让整个进程崩掉，而"少一次线程切换"远比崩溃轻。
        }

        // 没有 WPF 应用上下文时直接执行，界面集合由调用线程修改（无绑定时无害）。
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.InvokeAsync(action);
        }
        catch (Exception)
        {
            // Dispatcher 已关闭（应用退出中）时投递必然失败：退化为直接执行，
            // 保证最后一个任务的收尾输出不会因为窗口正在关闭而丢掉。
            action();
        }
    }
}
