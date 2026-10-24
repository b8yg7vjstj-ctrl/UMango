// SPDX-License-Identifier: Apache-2.0
//
// 本文件的结构参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/IoC/（14 文件 1,325 行，Apache-2.0）。
//
// 采用时做的改动（详见本仓库既定的「语义重写」做法）：
//   1. **只取「生命周期编排」这一层**，不取依赖注入的**源代码生成**部分。
//      PCL 的 IoC 依赖 `DependencyCollectorAttribute` + `DependencyInjectionPointAttribute`
//      + partial 类 + 源生成器（`PCL.Core/App/IoC/DependencyGroups.cs` 等），
//      那套机制要求使用者把类拆成 partial、写 [DependencyInjectionPoint] 标记方法，
//      对 UMango 现有的手写 `AppServices` 静态门面反而不合适 ——
//      静态门面已经在做「按需创建 + 懒加载」，DI 容器会与之重复。
//   2. PCL 的 `LifecycleState` 状态机里，有几个状态是 Minecraft 专属的
//      （`ModpackInstalling`、`UiLoad` 之类），UMango 只保留通用的那几个。
//   3. PCL 的生命周期服务是**通过反射扫描整个程序集**自动发现的
//      （`LifecycleServiceAttribute` + `AppDomain.CurrentDomain.GetAssemblies()`），
//      反射扫描在单测里不可控（会捞到测试程序集里的类型）。
//      UMango 改为**显式注册**：谁构造谁登记，顺序与内容都可见、可测。
//   4. PCL 的 `LifecycleContext` 带着 `RequestExit`、日志、`PropertyAccessor` 等一堆
//      与日志系统耦合的成员；UMango 的版本只保留「请求退出」这一个能力，
//      并把退出请求做成一个可观察事件，由 App 层决定怎么处理。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.Lifecycle;

/// <summary>生命周期阶段。名称与 PCL 保持一致，便于对照原版文档。</summary>
public enum LifecycleState
{
    /// <summary>不自动运行，由调用方手动控制。</summary>
    Manual = 0,

    /// <summary>预加载：不需要基本组件就能做的事（单实例检测等）。</summary>
    BeforeLoading,

    /// <summary>加载：基本组件初始化（日志、设置项等）。</summary>
    Loading,

    /// <summary>加载结束：功能性组件初始化。</summary>
    Loaded,

    /// <summary>窗口创建：主窗口内容初始化。</summary>
    WindowCreating,

    /// <summary>窗口创建结束：依赖窗口的事件（首次提示、主题刷新等）。</summary>
    WindowCreated,

    /// <summary>正在运行：程序正常运行后的工作。</summary>
    Running,

    /// <summary>尝试关闭：可以在这里阻止退出。</summary>
    Closing,

    /// <summary>关闭程序：保存状态、释放资源。</summary>
    Exiting,
}

/// <summary>生命周期服务。</summary>
public interface ILifecycleService
{
    /// <summary>全局唯一标识（小写字母 + 连字符）。</summary>
    string Identifier { get; }

    /// <summary>友好名称，用于日志。</summary>
    string Name { get; }

    /// <summary>是否支持异步启动。同一状态下会先同步启动不支持异步的服务。</summary>
    bool SupportAsync { get; }

    /// <summary>启动。</summary>
    Task StartAsync();

    /// <summary>停止。</summary>
    Task StopAsync();
}

/// <summary>注册服务时声明的元信息。</summary>
/// <param name="State">在哪个阶段启动。</param>
/// <param name="Priority">同一阶段内的优先级，**数值越大越先启动**。</param>
public readonly record struct ServiceRegistration(LifecycleState State, int Priority = 0);

/// <summary>注册一个生命周期服务。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LifecycleServiceAttribute(LifecycleState state) : Attribute
{
    /// <summary>启动阶段。</summary>
    public LifecycleState State { get; } = state;

    /// <summary>启动优先级（数值越大越先启动）。</summary>
    public int Priority { get; init; }
}

/// <summary>生命周期的通用基类：把同步的 Start/Stop 包装成接口。</summary>
public abstract class LifecycleServiceBase : ILifecycleService
{
    /// <inheritdoc />
    public abstract string Identifier { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public virtual bool SupportAsync => true;

    /// <summary>启动逻辑。</summary>
    public virtual void Start()
    {
    }

    /// <summary>停止逻辑。</summary>
    public virtual void Stop()
    {
    }

    /// <inheritdoc />
    public Task StartAsync()
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }
}

/// <summary>生命周期上下文：服务可以请求退出程序。</summary>
public sealed class LifecycleContext
{
    private int _exitRequested;

    /// <summary>是否有人请求退出。</summary>
    public bool ExitRequested => Volatile.Read(ref _exitRequested) != 0;

    /// <summary>请求退出。多次调用只算一次。</summary>
    public void RequestExit()
    {
        Interlocked.Exchange(ref _exitRequested, 1);
        ExitRequestedChanged?.Invoke();
    }

    /// <summary>退出请求状态变化时触发。</summary>
    public event Action? ExitRequestedChanged;
}

/// <summary>
/// 生命周期管理器：按阶段启动与停止服务。
///
/// 与 PCL 的差异见本文件头部的说明：显式注册（不做反射扫描）、
/// 状态机只保留通用阶段、上下文只保留「请求退出」。
/// </summary>
public sealed class LifecycleManager
{
    private readonly List<(ILifecycleService Service, ServiceRegistration Registration)> _services = [];
    /// <summary>已启动的服务，**按启动顺序**记录（停止时要用它的逆序）。</summary>
    private readonly List<ILifecycleService> _started = [];
    private readonly Lock _lock = new();

    /// <summary>上下文。</summary>
    public LifecycleContext Context { get; } = new();

    /// <summary>已注册的服务。</summary>
    public IReadOnlyList<ILifecycleService> RegisteredServices
    {
        get { lock (_lock) { return _services.Select(s => s.Service).ToArray(); } }
    }

    /// <summary>
    /// 注册服务。
    /// </summary>
    /// <param name="service">服务实例。</param>
    /// <param name="registration">注册元信息。</param>
    public void Register(ILifecycleService service, ServiceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_lock)
        {
            if (_services.Any(s => string.Equals(s.Service.Identifier, service.Identifier, StringComparison.Ordinal)))
                throw new InvalidOperationException($"生命周期标识重复：{service.Identifier}");
            _services.Add((service, registration));
        }
    }

    /// <summary>按类上的 <see cref="LifecycleServiceAttribute"/> 注册。</summary>
    public void Register(ILifecycleService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var attribute = service.GetType().GetCustomAttributes(typeof(LifecycleServiceAttribute), false)
            .OfType<LifecycleServiceAttribute>().FirstOrDefault();
        Register(service, new ServiceRegistration(attribute?.State ?? LifecycleState.Manual, attribute?.Priority ?? 0));
    }

    /// <summary>取消注册一个服务。</summary>
    public bool Unregister(ILifecycleService service)
    {
        lock (_lock)
        {
            var index = _services.FindIndex(s => ReferenceEquals(s.Service, service));
            if (index < 0)
                return false;
            _services.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// 启动某个阶段的服务。
    ///
    /// 顺序规则（沿用 PCL 的约定）：
    ///   1. 同阶段内按 <see cref="ServiceRegistration.Priority"/> **降序**（数值大的先启动）；
    ///   2. 先启动不支持异步的服务，再启动支持异步的（同步的失败能更早暴露）。
    /// </summary>
    public async Task StartStageAsync(LifecycleState stage, CancellationToken ct = default)
    {
        List<ILifecycleService> targets;
        lock (_lock)
        {
            targets = _services
                .Where(s => s.Registration.State == stage)
                .OrderByDescending(s => s.Registration.Priority)
                .Select(s => s.Service)
                .ToList();
        }

        foreach (var service in targets.Where(s => !s.SupportAsync))
            await StartOneAsync(service, ct);

        foreach (var service in targets.Where(s => s.SupportAsync))
            await StartOneAsync(service, ct);
    }

    /// <summary>
    /// 停止所有已启动的服务。
    ///
    /// 顺序：**启动的逆序**（后启动的先停），同阶段内也是启动顺序的逆序。
    /// PCL 的生命周期管理在退出时统一停止未停止的服务项，但**不保证逆序** ——
    /// 逆序更符合依赖关系（后启动的往往依赖先启动的）。
    /// </summary>
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        List<ILifecycleService> started;
        lock (_lock)
        {
            // 直接复制 _started —— 它本身就是启动顺序。
            // 早先的实现是从 _services（注册顺序）里筛出已启动的，
            // 那样得到的逆序是「注册顺序的逆序」，不是「启动顺序的逆序」，
            // 两者在「注册顺序与优先级顺序不一致」时结果不同。
            started = _started.ToList();
        }

        foreach (var service in Enumerable.Reverse(started))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await service.StopAsync();
            }
            catch
            {
                // 停止失败不应阻止其余服务释放。
            }
            lock (_lock) { _started.Remove(service); }
        }
    }

    /// <summary>已启动的服务数。</summary>
    public int StartedCount
    {
        get { lock (_lock) { return _started.Count; } }
    }

    private async Task StartOneAsync(ILifecycleService service, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await service.StartAsync();
        lock (_lock) { _started.Add(service); }
    }
}
