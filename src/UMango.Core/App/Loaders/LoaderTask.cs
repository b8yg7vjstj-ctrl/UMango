// SPDX-License-Identifier: Apache-2.0
//
// 泛型任务加载器。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs` 中的
// `LoaderTask<InputType, OutputType>`（自定义许可目录 → **只借鉴设计语义，独立重写**）。
//
// 对应关系：
//   · PCL `LoaderTask<,>` 的 `input` / `output` / `loadDelegate` → 本类同名成员；
//   · PCL `ShouldStart`（输入不同才重跑、reloadTimeout 内不重跑）→ 本类同名方法；
//   · PCL 用 `Task.Id` 比较判断"自己是否被新线程取代" → UMango 用**运行代次**。
//
// 相对 PCL 的改进：
//   ① **运行代次（generation）**：每次启动代次 +1。旧线程发现自己代次过期就
//      安静退出，**不写状态、不写输出**。PCL 用 `Task.Id` 比对，等价但更绕。
//   ② `Abort` 用 `CancellationToken` 取消，不再抛 `ThreadInterruptedException`。
//   ③ `StartGetInput` 的陷阱修正：PCL 用 `EqualityComparer<T>.Default.Equals(input, default)`
//      判断"输入没传"，**值类型恰好等于默认值时会误判**（PCL 自己在注释里承认这是陷阱）。
//      UMango 改成用 `HasInput` 显式标志区分"没传"和"传了默认值"。

using System;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.App.Loaders;

/// <summary>
/// 执行单个委托的加载器。
/// </summary>
/// <typeparam name="TInput">输入类型。</typeparam>
/// <typeparam name="TOutput">输出类型。</typeparam>
public sealed class LoaderTask<TInput, TOutput> : LoaderBase, IOutputLoader
{
    private readonly Action<LoaderTask<TInput, TOutput>> _loadDelegate;
    private readonly Func<TInput?>? _inputDelegate;

    private CancellationTokenSource? _cancellation;
    private int _generation;
    private bool _hasInput;
    private long _lastFinishedTimestamp;

    /// <summary>本次运行的输入。</summary>
    public TInput? Input { get; private set; }

    /// <summary>
    /// 本次运行的输出。
    ///
    /// **过时运行的写入会被丢弃**：被强制重启取代的旧运行，如果在其结束前写了
    /// 输出，那个输出**不会**覆盖新运行的结果。
    ///
    /// PCL 原版的做法相同 —— 它在旧线程结束时记
    /// 「已中断但线程正常运行至结束，输出被弃用（最新线程：…）」，即旧输出作废。
    /// UMango 把这个语义做成了**结构性保证**（靠运行代次）而不是靠日志说明。
    /// </summary>
    public TOutput? Output
    {
        get => _output;
        set
        {
            // `_writingGeneration.Value` 为 0 表示"不是从加载器委托里写的"
            // （例如外部代码直接赋值）→ 允许。
            if (_writingGeneration.Value != 0 && _writingGeneration.Value != Volatile.Read(ref _generation))
                return; // 过时运行 → 丢弃
            _output = value;
        }
    }

    private TOutput? _output;
    private readonly AsyncLocal<int> _writingGeneration = new();

    /// <summary>最近一次运行的输出（非泛型接口，供 <see cref="LoaderCombo"/> 取用）。</summary>
    public object? LastOutput => Output;

    /// <summary>
    /// 取消令牌：加载器被中止时它会被取消。
    /// 加载器应把它传给真正的 I/O 操作，才能真正停下来。
    /// </summary>
    public CancellationToken AbortedToken => _cancellation?.Token ?? new CancellationToken(true);

    /// <summary>最近一次成功完成的时间戳（毫秒）。</summary>
    public long LastFinishedTimestamp => Interlocked.Read(ref _lastFinishedTimestamp);

    /// <summary>
    /// 「输入相同则不重跑」的超时时间（毫秒）。
    /// -1 表示永不过期；0 表示每次都重跑。
    /// </summary>
    public int ReloadTimeout { get; set; } = -1;

    /// <summary>是否应当在启动前重新获取输入（PCL 的 `inputDelegate` 语义）。</summary>
    public Func<TInput?>? InputDelegate => _inputDelegate;

    /// <summary>构造一个任务加载器。</summary>
    public LoaderTask(string name, Action<LoaderTask<TInput, TOutput>> loadDelegate,
        Func<TInput?>? inputDelegate = null)
    {
        Name = name;
        _loadDelegate = loadDelegate ?? throw new ArgumentNullException(nameof(loadDelegate));
        _inputDelegate = inputDelegate;
    }

    /// <summary>
    /// 判断是否应当启动。
    ///
    /// 规则（与 PCL 一致）：
    ///   · 强制重启 → 总是启动；
    ///   · 输入与上次不同 → 启动；
    ///   · 正在运行或已完成，且未超过 <see cref="ReloadTimeout"/> → **不启动**；
    ///   · 否则启动。
    /// </summary>
    public bool ShouldStart(TInput? input, bool isForceRestart = false, bool ignoreReloadTimeout = false)
    {
        if (isForceRestart)
            return true;

        var inputChanged = !_hasInput || !EqualityComparer<TInput>.Default.Equals(input, Input);
        if (inputChanged)
            return true;

        var state = State;
        if (state is not (LoaderState.Running or LoaderState.Succeeded))
            return true;

        if (ignoreReloadTimeout)
            return false;

        if (ReloadTimeout == -1)
            return false;

        var last = LastFinishedTimestamp;
        if (last == 0L)
            return true;

        return Environment.TickCount64 - last >= ReloadTimeout;
    }

    /// <summary>启动（不比较输入，直接跑）。</summary>
    public override void Start(object? input = null, bool isForceRestart = false)
    {
        object? boxed = input;

        // 取输入：显式传了就用它；没传且有 inputDelegate 才去问。
        // 这里**不**用"值是否等于 default"来判断"没传"—— 那是 PCL 承认的陷阱。
        TInput? typed;
        if (boxed is null)
        {
            if (_inputDelegate is not null)
            {
                typed = _inputDelegate();
                _hasInput = true;
            }
            else
            {
                typed = default;
                _hasInput = false;
            }
        }
        else
        {
            // 类型不匹配不能直接强转 —— PCL 用 `Conversions.ToGenericParameter`
            // 做的是"能转就转，不能转就当没传"。这里同样处理：
            // 类型不符 → 视为未传（若配了 inputDelegate 则去问它）。
            if (boxed is TInput matched)
            {
                typed = matched;
                _hasInput = true;
            }
            else if (_inputDelegate is not null)
            {
                typed = _inputDelegate();
                _hasInput = true;
            }
            else
            {
                typed = default;
                _hasInput = false;
            }
        }

        if (!isForceRestart && !ShouldStart(typed))
            return;

        // 已在运行 → 先中止旧的，再起新的。
        if (State == LoaderState.Running)
            Abort();

        Input = typed;
        Output = default;
        ResetProgress();

        var generation = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        _cancellation = cts;

        State = LoaderState.Running;

        _ = Task.Run(async () =>
        {
            try
            {
                // 标记"本次写入属于哪一代"，供 Output 的过时写入检查使用。
                _writingGeneration.Value = generation;
                await Task.Run(() => _loadDelegate(this)).ConfigureAwait(false);

                // 代次过期说明有更新的运行取代了本次 —— 安静退出，不写状态。
                if (Volatile.Read(ref _generation) != generation)
                    return;

                State = LoaderState.Succeeded;
                Interlocked.Exchange(ref _lastFinishedTimestamp, Environment.TickCount64);
            }
            catch (OperationCanceledException)
            {
                if (Volatile.Read(ref _generation) != generation)
                    return;
                State = LoaderState.Aborted;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _generation) != generation)
                    return;
                Error = ex;
                State = LoaderState.Failed;
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    /// <summary>中止当前运行。</summary>
    public override void Abort()
    {
        // 还没开始的（等待态）也要能中止：复合链里某个子任务失败时，
        // 后续还没轮到的子任务处于 Waiting —— 若不改成 Aborted，它们会
        // 永远停在"等待"，界面上看起来像还在排队。
        // PCL 原版的 ModLoader.Abort 同样是直接置状态，不区分是否在跑。
        if (State == LoaderState.Waiting)
        {
            State = LoaderState.Aborted;
            return;
        }

        if (State != LoaderState.Running)
            return;

        // 代次 +1：正在跑的线程发现自己过期后会安静退出，不会把状态改成 Aborted
        // —— 因为那可能是用户取消了**旧**的一次运行，而新的一次已经在跑。
        Interlocked.Increment(ref _generation);

        var cts = _cancellation;
        _cancellation = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 运行已结束，忽略
        }

        State = LoaderState.Aborted;
    }
}
