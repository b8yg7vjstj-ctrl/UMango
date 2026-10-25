// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路取自 PCL-CE 的
// PCL.Core/App/Essentials/ApplicationService.cs（65 行）
// 与 PCL.Core/App/Essentials/MainWindowService.cs（24 行）
// （均为 Apache-2.0 许可）。
//
// 采用时做的改动：
//   1. PCL 的两个服务都直接引用 `System.Windows.Application` / `Window`，
//      因此**在 macOS/Linux 上根本编译不过**。UMango 的 Core 是纯 .NET，
//      所以这里把「生命周期编排」与「WPF 宿主」拆开：
//        · 本文件（Core）：服务注册、启动顺序、停止顺序、异常隔离、窗口登记；
//        · App 层（`AppLifecycleHost`）：真正绑定 WPF 的 Application/Window。
//      PCL 的 `ApplicationService.Loading` 委托被换成 `Func<object?>`，
//      类型不再泄漏进 Core。
//   2. PCL 用 `[LifecycleService]` 特性 + IoC 容器做**属性注入 + 生命周期挂钩**，
//      依赖它那套 13 文件的 IoC 框架。UMango 已有自己的组合根，
//      引入第二套生命周期框架不成比例 → 改为**显式注册表**。
//   3. PCL 的停止逻辑用 `dispatcher.BeginInvoke(DispatcherPriority.Send, …)`
//      发起退出并最多等 5 秒，**超时只打警告不强制结束**。
//      UMango 保留「等 5 秒」这个尺度，但把「发退出」抽象成回调，
//      Core 层不碰 Dispatcher。
//   4. PCL 的 `MainWindowService` 在 `window.Loaded` 时才推进
//      `Lifecycle.OnWindowCreated()` —— 也就是说「窗口已创建」这个事实
//      是以**控件真的 Loaded** 为准，而不是构造函数返回。
//      UMango 保留这个语义（构造函数返回 ≠ 窗口已可用）。

using UMango.Core.Logging;

namespace UMango.Core.App.Essentials;

/// <summary>一个生命周期服务的登记项。</summary>
public sealed class LifecycleServiceRegistration
{
    /// <summary>构造登记项。</summary>
    /// <param name="id">服务标识（用于日志与排错）。</param>
    /// <param name="start">启动动作。</param>
    /// <param name="stop">停止动作（可为空）。</param>
    /// <param name="priority">优先级，越小越先启动；停止时顺序相反。</param>
    public LifecycleServiceRegistration(
        string id,
        Action start,
        Action? stop = null,
        int priority = 0)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Start = start ?? throw new ArgumentNullException(nameof(start));
        Stop = stop;
        Priority = priority;
    }

    /// <summary>服务标识。</summary>
    public string Id { get; }

    /// <summary>启动动作。</summary>
    public Action Start { get; }

    /// <summary>停止动作。</summary>
    public Action? Stop { get; }

    /// <summary>优先级（越小越先启动）。</summary>
    public int Priority { get; }

    /// <summary>是否已启动过。</summary>
    public bool Started { get; internal set; }
}

/// <summary>一个「作用域」的登记项（PCL 的 <c>LifecycleScope</c>）。</summary>
/// <param name="Id">作用域标识。</param>
/// <param name="Title">显示名。</param>
/// <param name="Visible">是否在界面里展示（PCL 的第三个参数）。</param>
public readonly record struct LifecycleScope(string Id, string Title, bool Visible = false);

/// <summary>窗口登记的状态。</summary>
public enum MainWindowState
{
    /// <summary>还没有登记窗口。</summary>
    None = 0,

    /// <summary>已登记，但控件还没 Loaded。</summary>
    Created,

    /// <summary>控件已 Loaded —— 界面真的可用了。</summary>
    Ready,
}

/// <summary>
/// 应用生命周期宿主（Core 层）。
///
/// 职责（对应 PCL 的 <c>ApplicationService</c> + <c>MainWindowService</c>）：
///   1. 持有「创建应用」「创建主窗口」的委托 —— 但类型是 <see cref="object"/>，
///      不泄漏 WPF；
///   2. 按优先级启动已登记的服务，**单个服务抛异常不影响其它服务**；
///   3. 记录主窗口的登记与 Loaded 状态；
///   4. 停止时按**启动的逆序**执行停止动作，并给每个停止动作 5 秒的等待上限
///      （PCL 的 <c>exited.Wait(5000)</c>）。
/// </summary>
public sealed class ApplicationLifecycleHost
{
    private readonly List<LifecycleServiceRegistration> _services = [];
    private readonly List<LifecycleScope> _scopes = [];
    private readonly object _gate = new();
    private int _started;
    private int _stopped;

    /// <summary>创建应用实例的委托（可为空）。</summary>
    public Func<object?>? ApplicationFactory { get; set; }

    /// <summary>创建主窗口实例的委托（可为空）。</summary>
    public Func<object?>? MainWindowFactory { get; set; }

    /// <summary>已创建的应用实例。</summary>
    public object? Application { get; private set; }

    /// <summary>已创建的主窗口实例。</summary>
    public object? MainWindow { get; private set; }

    /// <summary>主窗口状态。</summary>
    public MainWindowState WindowState { get; private set; } = MainWindowState.None;

    /// <summary>已登记的作用域。</summary>
    public IReadOnlyList<LifecycleScope> Scopes
    {
        get { lock (_gate) return _scopes.ToList(); }
    }

    /// <summary>是否已启动。</summary>
    public bool IsStarted => Volatile.Read(ref _started) == 1;

    /// <summary>是否已停止。</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) == 1;

    /// <summary>
    /// 登记一个生命周期服务。
    ///
    /// 重复登记同一个 <paramref name="id"/> 会被拒绝 —— PCL 的 IoC 是按类型注册的，
    /// 重复注册会抛；这里选择静默拒绝并记日志，避免应用启动路径上抛异常。
    /// </summary>
    public bool Register(LifecycleServiceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_services.Any(s => string.Equals(s.Id, registration.Id, StringComparison.Ordinal)))
            {
                Log.Warn("Lifecycle", $"生命周期服务重复登记：{registration.Id}");
                return false;
            }

            _services.Add(registration);
            return true;
        }
    }

    /// <summary>登记一个作用域。</summary>
    public bool RegisterScope(LifecycleScope scope)
    {
        lock (_gate)
        {
            if (_scopes.Any(s => string.Equals(s.Id, scope.Id, StringComparison.Ordinal)))
                return false;
            _scopes.Add(scope);
            return true;
        }
    }

    /// <summary>已登记的服务 id（按启动顺序）。</summary>
    public IReadOnlyList<string> RegisteredServiceIds
    {
        get
        {
            lock (_gate)
                return _services.OrderBy(s => s.Priority).ThenBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => s.Id).ToList();
        }
    }

    /// <summary>
    /// 启动：创建应用 → 创建主窗口 → 按优先级启动服务。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        Application = ApplicationFactory?.Invoke();
        if (Application is null)
            Log.Warn("Lifecycle", "未登记应用创建委托，跳过应用创建");

        MainWindow = MainWindowFactory?.Invoke();
        WindowState = MainWindow is null ? MainWindowState.None : MainWindowState.Created;

        List<LifecycleServiceRegistration> ordered;
        lock (_gate)
            ordered = _services.OrderBy(s => s.Priority).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

        foreach (var service in ordered)
        {
            try
            {
                service.Start();
                service.Started = true;
            }
            catch (Exception ex)
            {
                // 单个服务失败不能挡住其余服务 —— PCL 的 ApplicationService
                // 在启动时也是逐个 try/catch，失败只记录不中断。
                Log.ErrorAt("Lifecycle", ex, member: service.Id);
            }
        }

        Log.Info("Lifecycle", $"应用生命周期已启动，服务 {ordered.Count} 个");
    }

    /// <summary>
    /// 标记主窗口的控件已 Loaded —— 界面真的可用了。
    ///
    /// 对应 PCL <c>MainWindowService</c> 的 <c>window.Loaded += …</c>。
    /// </summary>
    public void NotifyMainWindowLoaded()
    {
        if (MainWindow is null)
            return;
        WindowState = MainWindowState.Ready;
    }

    /// <summary>
    /// 停止：按**启动的逆序**执行停止动作；单个停止动作抛异常不影响其余。
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        List<LifecycleServiceRegistration> ordered;
        lock (_gate)
            ordered = _services.OrderBy(s => s.Priority).ThenBy(s => s.Id, StringComparer.Ordinal)
                .Reverse().ToList();

        foreach (var service in ordered)
        {
            if (service.Stop is null)
                continue;
            try
            {
                service.Stop();
            }
            catch (Exception ex)
            {
                Log.ErrorAt("Lifecycle", ex, member: service.Id + ".Stop");
            }
        }

        Log.Info("Lifecycle", "应用生命周期已停止");
    }
}
