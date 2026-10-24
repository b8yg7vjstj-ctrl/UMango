// SPDX-License-Identifier: Apache-2.0
//
// 复合加载器：把多个加载器串成流水线。
//
// **设计来源**：PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModLoader.cs` 中的
// `LoaderCombo`（自定义许可目录 → **只借鉴设计语义，独立重写，不复制代码**）。
//
// 对应关系：
//   · PCL `LoaderCombo.loaders` 子加载器列表 → 本类 `Loaders`；
//   · PCL 的加权进度（Σ权重×进度 / Σ权重） → 本类同名算法；
//   · PCL 的「前一个的输出作为后一个的输入」→ 本类 `PassOutputToNext`；
//   · PCL 的「子任务失败 → 整条链失败并中止其余」→ 本类同名行为；
//   · PCL 的 `GetLoaderList`（摊平嵌套 Combo 给界面显示）→ 本类同名方法。
//
// 相对 PCL 的改进：
//   ① PCL 的 `Update` 靠 `dynamic` 做类型匹配（`LoaderTask<,>` 的输出类型不匹配时
//      抛 `RuntimeBinderException`）；UMango 用 `LoaderTask` 的非泛型基类接口
//      `IOutputLoader` 显式取输出，**不做 dynamic**。
//   ② PCL 的子任务状态监听通过 `hasOnStateChangedThread` 标志位"订阅才触发"，
//      标志位是**每个子加载器上的全局状态**，容易被外部误设；UMango 直接订阅事件。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UMango.Core.App.Loaders;

/// <summary>能产出输出的加载器（非泛型接口，避免 dynamic）。</summary>
public interface IOutputLoader
{
    /// <summary>最近一次运行的输出（类型为 object，调用方自行转型）。</summary>
    object? LastOutput { get; }
}

/// <summary>
/// 复合加载器：按 <see cref="Loaders"/> 顺序依次执行。
/// </summary>
public sealed class LoaderCombo : LoaderBase
{
    private readonly List<LoaderBase> _loaders = [];

    /// <summary>子加载器列表（只读视图）。</summary>
    public IReadOnlyList<LoaderBase> Loaders => _loaders;

    /// <summary>子加载器是否应阻塞后续（PCL 的 `block` 语义）。</summary>
    public bool Block { get; set; } = true;

    /// <summary>是否把前一个子加载器的输出作为后一个的输入。</summary>
    public bool PassOutputToNext { get; set; } = true;

    /// <summary>构造一个复合加载器。</summary>
    public LoaderCombo(string name, IEnumerable<LoaderBase>? loaders = null)
    {
        Name = name;
        if (loaders is null)
            return;

        foreach (var loader in loaders)
        {
            if (loader is null)
                continue;
            _loaders.Add(loader);
            loader.InitParent(this);
            loader.StateChanged += OnChildStateChanged;
        }
    }

    /// <summary>加入一个子加载器。</summary>
    public void Add(LoaderBase loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loaders.Add(loader);
        loader.InitParent(this);
        loader.StateChanged += OnChildStateChanged;
    }

    /// <summary>加权进度（与 PCL 一致：Σ权重×进度 / Σ权重）。</summary>
    public override double Progress
    {
        get
        {
            var state = State;
            if (state == LoaderState.Waiting)
                return 0d;
            if (state != LoaderState.Running)
                return 1d;

            double total = 0d;
            double finished = 0d;
            foreach (var loader in _loaders)
            {
                total += loader.ProgressWeight;
                finished += loader.ProgressWeight * loader.Progress;
            }

            return total == 0d ? 0d : finished / total;
        }
        set
        {
            // 复合加载器的进度由子加载器决定，不接受外部设置（与 PCL 一致）。
        }
    }

    /// <summary>启动整条链。</summary>
    public override void Start(object? input = null, bool isForceRestart = false)
    {
        if (State == LoaderState.Running)
            return;

        State = LoaderState.Running;

        if (isForceRestart)
        {
            foreach (var loader in _loaders)
                loader.Abort();
        }

        _ = Task.Run(() => RunChainAsync(input, isForceRestart));
    }

    private async Task RunChainAsync(object? input, bool isForceRestart)
    {
        object? current = input;

        foreach (var loader in _loaders)
        {
            if (State == LoaderState.Aborted)
                return;

            // 强制重启时把子加载器重置回等待态
            if (isForceRestart && loader.State is LoaderState.Succeeded or LoaderState.Failed)
                loader.Abort();

            try
            {
                // 注意：这里**只调 WaitForExitAsync**，不再单独调 Start ——
                // WaitForExitAsync 自己会 Start。之前两者都调，等于把子加载器
                // 启动了两遍（第二遍输入为空），会丢掉上一环传来的输出。
                await loader.WaitForExitAsync(PassOutputToNext ? current : null, null, isForceRestart, null)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 只有当本链还没有定论（仍在运行）时才改状态。
                // 子任务失败会先置 Failed 并级联中止其余子任务 —— 那时
                // 这里不能再把状态覆盖成 Aborted，否则真实错误就丢了。
                if (State == LoaderState.Running)
                {
                    State = LoaderState.Aborted;
                    AbortChildren();
                }
                return;
            }
            catch (Exception ex)
            {
                if (State == LoaderState.Running)
                {
                    Error = ex;
                    State = LoaderState.Failed;
                    AbortChildren();
                }
                return;
            }

            if (loader is IOutputLoader output)
                current = output.LastOutput;
        }

        State = LoaderState.Succeeded;
    }

    /// <summary>中止整条链（级联中止子加载器）。</summary>
    public override void Abort()
    {
        if (State is not (LoaderState.Running or LoaderState.Waiting))
            return;

        AbortChildren();
        State = LoaderState.Aborted;
    }

    private void AbortChildren()
    {
        foreach (var loader in _loaders)
            loader.Abort();
    }

    private void OnChildStateChanged(LoaderBase loader, LoaderState newState, LoaderState oldState)
    {
        switch (newState)
        {
            case LoaderState.Aborted:
                // 子任务被中止 → 整条链中止
                if (State == LoaderState.Running)
                {
                    AbortChildren();
                    State = LoaderState.Aborted;
                }
                break;

            case LoaderState.Failed:
                if (State == LoaderState.Running)
                {
                    Error = loader.Error ?? new InvalidOperationException($"{loader.Name} 失败");
                    AbortChildren();
                    State = LoaderState.Failed;
                }
                break;
        }
    }

    /// <summary>摊平嵌套的复合加载器，给界面显示用。</summary>
    public List<LoaderBase> GetLoaderList(bool requireShow = true)
    {
        var list = new List<LoaderBase>();
        Collect(this, list, requireShow);
        return list;
    }

    private static void Collect(LoaderCombo combo, List<LoaderBase> list, bool requireShow)
    {
        foreach (var loader in combo.Loaders)
        {
            if (loader.Show || !requireShow)
                list.Add(loader);
            if (loader is LoaderCombo nested)
                Collect(nested, list, requireShow);
        }
    }
}
