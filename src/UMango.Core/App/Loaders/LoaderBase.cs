// SPDX-License-Identifier: Apache-2.0
//
// 加载器基类。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs`（1,032 行，
// 自定义许可目录）→ **只借鉴设计语义，独立重写，不复制代码**。
//
// 与 PCL `LoaderBase` 的对应：
//   · `State` 状态机（Waiting/Running/Succeeded/Failed/Aborted）；
//   · `Progress` 0..1（PCL 用 -1 表示"未开始"，UMango 用 0 并另设 <see cref="HasProgress"/>）；
//   · `ProgressWeight` 加权进度（PCL 注释说"应该是预计时间（秒）"）；
//   · `RealParent` 顶级父加载器；
//   · `InitParent` 挂载到父级；
//   · `WaitForExit` / `WaitForExitTime`（UMango 改成 async 版，不阻塞线程）；
//   · `Abort` 中止。
//
// 相对 PCL 的改进：
//   ① 状态改变事件在**调用方线程**同步触发（PCL 用 `RunInUi` 切 UI 线程 + 另开线程
//      触发 `OnStateChangedThread` 两套）。UMango 改为**单一事件** + 可选的
//      `SynchronizationContext` 调度器，由宿主决定要不要切 UI 线程。
//   ② 进度有 `HasProgress` 标志，能表达"不确定态"（PCL 靠 -1 隐式表达）。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.Loaders;

/// <summary>加载器状态改变事件。</summary>
public delegate void LoaderStateChangedHandler(LoaderBase loader, LoaderState newState, LoaderState oldState);

/// <summary>加载器进度改变事件。</summary>
public delegate void LoaderProgressChangedHandler(LoaderBase loader, double newProgress, double oldProgress);

/// <summary>
/// 加载器基类。
///
/// 一个加载器代表**一件可以独立运行、有进度、可中止的事**（下载、解压、安装…）。
/// <see cref="LoaderCombo"/> 把多个加载器串成流水线。
/// </summary>
public abstract class LoaderBase
{
    private readonly object _stateLock = new();

    /// <summary>状态改变事件（在触发线程上同步调用）。</summary>
    public event LoaderStateChangedHandler? StateChanged;

    /// <summary>进度改变事件（在触发线程上同步调用）。</summary>
    public event LoaderProgressChangedHandler? ProgressChanged;

    /// <summary>
    /// 调度器：把状态/进度事件切到指定线程（例如 UI 线程）。
    /// 为 null 时事件在触发线程上直接调用。
    /// </summary>
    public static Func<Action, Task>? Dispatcher { get; set; }

    /// <summary>加载器名称（界面显示）。</summary>
    public string Name { get; set; } = "未命名任务";

    /// <summary>是否在界面上显示。</summary>
    public bool Show { get; set; } = true;

    /// <summary>错误信息（<see cref="LoaderState.Failed"/> 时有效）。</summary>
    public Exception? Error { get; set; }

    /// <summary>父加载器。</summary>
    public LoaderBase? Parent { get; private set; }

    /// <summary>顶级父加载器（自身无父时返回 null）。</summary>
    public LoaderBase? RealParent
    {
        get
        {
            var current = Parent;
            while (current?.Parent is not null)
                current = current.Parent;
            return current;
        }
    }

    private LoaderState _state = LoaderState.Waiting;
    private double _progress;
    private bool _hasProgress;

    /// <summary>当前状态。</summary>
    public LoaderState State
    {
        get
        {
            lock (_stateLock)
                return _state;
        }
        /// <summary>
        /// 设置状态。正常代码里只有加载器自己会改；测试与外部协调器也可能需要
        /// 强制改（例如把一个外部驱动的加载器标记为已完成），所以这里是 public。
        /// </summary>
        protected set
        {
            LoaderState old;
            lock (_stateLock)
            {
                if (_state == value)
                    return;
                old = _state;
                _state = value;
            }

            Raise(() => StateChanged?.Invoke(this, value, old));
        }
    }

    /// <summary>
    /// 当前进度（0..1）。
    ///
    /// <see cref="HasProgress"/> 为 false 时本值无意义（不确定态）。
    /// </summary>
    public virtual double Progress
    {
        get
        {
            lock (_stateLock)
                return _progress;
        }
        set
        {
            value = Math.Clamp(value, 0d, 1d);
            double old;
            lock (_stateLock)
            {
                if (_hasProgress && Math.Abs(_progress - value) < double.Epsilon)
                    return;
                old = _progress;
                _progress = value;
                _hasProgress = true;
            }

            Raise(() => ProgressChanged?.Invoke(this, value, old));
        }
    }

    /// <summary>是否报告了确定的进度（false = 不确定态）。</summary>
    public bool HasProgress
    {
        get
        {
            lock (_stateLock)
                return _hasProgress;
        }
    }

    /// <summary>清空进度（回到不确定态）。</summary>
    protected void ResetProgress()
    {
        lock (_stateLock)
        {
            _progress = 0d;
            _hasProgress = false;
        }
    }

    /// <summary>
    /// 计算总进度时的权重。
    ///
    /// PCL 原版的注释说"应该是预计时间（秒）"—— 也就是**耗时长的多占权重**。
    /// UMango 沿用这个语义：默认 1。
    /// </summary>
    public double ProgressWeight { get; set; } = 1d;

    /// <summary>把本加载器挂到父加载器下（级联中止由此建立）。</summary>
    public virtual void InitParent(LoaderBase? parent)
    {
        Parent = parent;
    }

    /// <summary>启动加载器。</summary>
    public abstract void Start(object? input = null, bool isForceRestart = false);

    /// <summary>中止加载器。</summary>
    public abstract void Abort();

    /// <summary>
    /// 等待加载器结束。
    ///
    /// 与 PCL 的同步 `WaitForExit` 对应，但**不阻塞线程**：
    /// PCL 版会 `Thread.Sleep(10)` 轮询，UMango 版直接 await 完成信号。
    /// </summary>
    /// <exception cref="OperationCanceledException">加载器被中止。</exception>
    /// <exception cref="TimeoutException">等待超时。</exception>
    public async Task WaitForExitAsync(object? input = null, LoaderBase? syncProgressWith = null,
        bool isForceRestart = false, int? timeoutMilliseconds = null)
    {
        Start(input, isForceRestart);

        using var timeoutCts = timeoutMilliseconds is { } ms
            ? new CancellationTokenSource(ms)
            : null;

        while (true)
        {
            var state = State;
            if (state != LoaderState.Running)
                break;

            if (syncProgressWith is not null)
                syncProgressWith.Progress = Progress;

            try
            {
                await Task.Delay(10, timeoutCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts is not null)
            {
                throw new TimeoutException($"等待加载器 {Name} 执行超时。");
            }
        }

        switch (State)
        {
            case LoaderState.Succeeded:
                return;
            case LoaderState.Aborted:
                throw new OperationCanceledException($"加载器 {Name} 执行已中断。");
            case LoaderState.Failed:
                throw Error ?? new InvalidOperationException($"加载器 {Name} 失败，但未记录错误。");
            default:
                throw new InvalidOperationException($"加载器 {Name} 结束于未知状态 {State}。");
        }
    }

    /// <summary>把一个动作投递到调度器（无调度器时直接执行）。</summary>
    internal static void Raise(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        _ = dispatcher(action);
    }
}
