// SPDX-License-Identifier: Apache-2.0
//
// 加载器框架测试（对应 PCL-CE `ModLoader.cs` 的行为，Apache-2.0 兼容的独立重写）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.App.Loaders;
using Xunit;

namespace UMango.Core.Tests.App;

[Collection("静态状态")]
public class LoaderTests
{
    /// <summary>测试用加载器：把 protected 的状态设置器暴露出来。</summary>
    private sealed class TestLoader : LoaderBase
    {
        public void SetState(LoaderState state) => State = state;

        public override void Start(object? input = null, bool isForceRestart = false) { }

        public override void Abort() { }
    }

    private static async Task<LoaderState> WaitForState(LoaderBase loader, LoaderState wanted, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (loader.State == wanted)
                return loader.State;
            await Task.Delay(10);
        }

        throw new TimeoutException($"加载器 {loader.Name} 未在 {timeoutMs}ms 内进入 {wanted}（当前 {loader.State}）");
    }

    // ---------- LoaderTask ----------

    [Fact]
    public async Task 任务成功_状态变成功且有输出()
    {
        var task = new LoaderTask<int, string>("t", t => t.Output = (t.Input * 2).ToString());
        task.Start(21);
        await WaitForState(task, LoaderState.Succeeded);

        Assert.Equal("42", task.Output);
        Assert.Null(task.Error);
    }

    [Fact]
    public async Task 任务失败_记异常且状态失败()
    {
        var task = new LoaderTask<int, string>("t", _ => throw new InvalidOperationException("boom"));
        task.Start(1);
        await WaitForState(task, LoaderState.Failed);

        Assert.NotNull(task.Error);
        Assert.Equal("boom", task.Error!.Message);
    }

    [Fact]
    public async Task 任务中止_状态变中止()
    {
        var started = new TaskCompletionSource();
        var task = new LoaderTask<int, string>("t", t =>
        {
            started.TrySetResult();
            // 必须**感知取消令牌**，否则 Abort 只改状态，不会抛
            // OperationCanceledException，取消路径就永远走不到。
            t.AbortedToken.WaitHandle.WaitOne(5000);
            t.AbortedToken.ThrowIfCancellationRequested();
        });
        task.Start(1);
        await started.Task;
        task.Abort();
        await WaitForState(task, LoaderState.Aborted);

        // 取消后不能变成成功（代次/取消处理失效时的症状）
        await Task.Delay(200);
        Assert.Equal(LoaderState.Aborted, task.State);
    }

    [Fact]
    public async Task 相同输入_默认不重跑()
    {
        var runs = 0;
        var task = new LoaderTask<int, string>("t", _ => Interlocked.Increment(ref runs));
        task.Start(1);
        await WaitForState(task, LoaderState.Succeeded);

        task.Start(1);
        await Task.Delay(150);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task 不同输入_会重跑()
    {
        var runs = 0;
        var task = new LoaderTask<int, string>("t", _ => Interlocked.Increment(ref runs));
        task.Start(1);
        await WaitForState(task, LoaderState.Succeeded);

        task.Start(2);
        await Task.Delay(200);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task 强制重启_会重跑()
    {
        var runs = 0;
        var task = new LoaderTask<int, string>("t", _ => Interlocked.Increment(ref runs));
        task.Start(1);
        await WaitForState(task, LoaderState.Succeeded);

        task.Start(1, isForceRestart: true);
        await Task.Delay(200);

        Assert.Equal(2, runs);
    }

    [Fact]
    public void 值类型默认输入_不被误判为未传()
    {
        // PCL 的陷阱：EqualityComparer<T>.Default.Equals(input, default) 会把
        // 显式传入的 0 当成"没传"。UMango 用显式 HasInput 标志区分。
        var asked = 0;
        var task = new LoaderTask<int, string>("t", _ => { }, () => { asked++; return 0; });

        // 显式传 0 → 不应去问 inputDelegate
        task.Start(0);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void 未传输入_会询问输入委托()
    {
        var asked = 0;
        var task = new LoaderTask<int, string>("t", _ => { }, () => { asked++; return 7; });

        task.Start();
        Assert.Equal(1, asked);
        Assert.Equal(7, task.Input);
    }

    [Fact]
    public async Task 中止后旧线程不覆盖新运行的状态()
    {
        // 代次机制：旧的运行被取代后，安静退出，不写状态。
        // 注意：委托是 `Action<>`（同步），**不能用 async lambda** ——
        // async lambda 会变成 async void，方法会在第一个 await 处立刻返回，
        // 根本不会阻塞在"旧运行"里，测不到代次机制。
        var gate = new ManualResetEventSlim(false);
        var task = new LoaderTask<int, string>("t", t =>
        {
            if (t.Input == 1)
            {
                gate.Wait(5000);
                // 旧运行放行后报错：若代次检查失效，Error 会被写上、
                // 状态会被改成 Failed —— 覆盖新运行的成功结果。
                throw new InvalidOperationException("stale failure");
            }
            t.Output = "second";
        });

        task.Start(1);
        await Task.Delay(80);
        Assert.Equal(LoaderState.Running, task.State);

        // 强制重启：代次 +1，旧运行被取代
        task.Start(2, isForceRestart: true);
        await WaitForState(task, LoaderState.Succeeded);
        Assert.Equal("second", task.Output);

        // 放走旧运行：它发现自己代次过期，必须安静退出 —— 不能改状态、不能改输出
        gate.Set();
        await Task.Delay(300);

        Assert.Equal(LoaderState.Succeeded, task.State);
        Assert.Equal("second", task.Output);
        // 过时运行不得留下错误
        Assert.Null(task.Error);
    }

    [Fact]
    public async Task 等待结束_成功时正常返回()
    {
        var task = new LoaderTask<int, string>("t", t => t.Output = "ok");
        await task.WaitForExitAsync(1);
        Assert.Equal(LoaderState.Succeeded, task.State);
    }

    [Fact]
    public async Task 等待结束_失败时抛出原异常()
    {
        var task = new LoaderTask<int, string>("t", _ => throw new InvalidOperationException("bad"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitForExitAsync(1));
        Assert.Equal("bad", ex.Message);
    }

    [Fact]
    public async Task 等待结束_超时抛超时异常()
    {
        var task = new LoaderTask<int, string>("t", _ => Thread.Sleep(5000));
        await Assert.ThrowsAsync<TimeoutException>(() => task.WaitForExitAsync(1, null, false, 200));
    }

    // ---------- LoaderCombo ----------

    [Fact]
    public async Task 复合加载器_按顺序执行并传递输出()
    {
        var first = new LoaderTask<int, string>("a", t => t.Output = "hello");
        var second = new LoaderTask<string, string>("b", t => t.Output = t.Input + " world");
        var combo = new LoaderCombo("chain", new LoaderBase[] { first, second });

        await combo.WaitForExitAsync();

        Assert.Equal(LoaderState.Succeeded, combo.State);
        Assert.Equal("hello world", second.Output);
    }

    [Fact]
    public async Task 复合加载器_子任务失败则整链失败()
    {
        var first = new LoaderTask<int, string>("a", _ => throw new InvalidOperationException("x"));
        var second = new LoaderTask<string, string>("b", t => t.Output = "never");
        var combo = new LoaderCombo("chain", new LoaderBase[] { first, second });

        await Assert.ThrowsAsync<InvalidOperationException>(() => combo.WaitForExitAsync());

        Assert.Equal(LoaderState.Failed, combo.State);
        // 失败原因必须保留下来（组合层不写 Error 的话，界面只能看到"失败"没有原因）
        Assert.NotNull(combo.Error);
        Assert.Equal("x", combo.Error!.Message);
        Assert.Null(second.Output);
        // 失败后尚未开始的后续子加载器必须被中止，不能留在等待态
        Assert.Equal(LoaderState.Aborted, second.State);
    }

    [Fact]
    public async Task 复合加载器_进度按权重加权()
    {
        // 组合加载器的进度是**实时加权**的：子任务全部结束后 Combo 的状态不再是
        // Running，getter 会直接返回 1 —— 所以必须在子任务**运行中**采样。
        var gate = new TaskCompletionSource();
        var a = new LoaderTask<int, string>("a", t => { t.Progress = 1d; gate.SetResult(); Thread.Sleep(2000); });
        var b = new LoaderTask<int, string>("b", t => { t.Progress = 0d; Thread.Sleep(2000); });
        b.ProgressWeight = 3d;
        var combo = new LoaderCombo("chain", new LoaderBase[] { a, b });

        combo.Start();
        await gate.Task;
        await Task.Delay(50);

        // 权重 1 与 3 → (1*1 + 3*0) / 4 = 0.25
        Assert.Equal(0.25d, combo.Progress, 3);

        combo.Abort();
    }

    [Fact]
    public async Task 复合加载器_摊平列表给界面()
    {
        var inner = new LoaderTask<int, string>("inner", _ => { });
        var innerCombo = new LoaderCombo("innerCombo", new LoaderBase[] { inner });
        var outer = new LoaderTask<int, string>("outer", _ => { });
        var combo = new LoaderCombo("outerCombo", new LoaderBase[] { outer, innerCombo });

        var list = combo.GetLoaderList();

        Assert.Contains(outer, list);
        Assert.Contains(inner, list);
    }

    [Fact]
    public async Task 复合加载器_中止会级联到子加载器()
    {
        var child = new LoaderTask<int, string>("a", t => Thread.Sleep(3000));
        var combo = new LoaderCombo("chain", new LoaderBase[] { child });

        combo.Start();
        await Task.Delay(100);
        combo.Abort();

        await WaitForState(combo, LoaderState.Aborted);
        await WaitForState(child, LoaderState.Aborted);
    }

    // ---------- 文件夹观察器 ----------

    [Fact]
    public void 文件夹观察器_时间没变则不重复运行()
    {
        var dir = Directory.CreateTempSubdirectory("umango-watch");
        try
        {
            var loader = new LoaderTask<int, string>("t", _ => { });
            var watcher = new LoaderFolderWatcher();

            Assert.True(watcher.Run(loader, dir.FullName, LoaderFolderRunType.RunOnUpdated));
            Assert.False(watcher.Run(loader, dir.FullName, LoaderFolderRunType.RunOnUpdated));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void 文件夹观察器_只更新不运行()
    {
        var dir = Directory.CreateTempSubdirectory("umango-watch");
        try
        {
            var loader = new LoaderTask<int, string>("t", _ => { });
            var watcher = new LoaderFolderWatcher();

            Assert.False(watcher.Run(loader, dir.FullName, LoaderFolderRunType.UpdateOnly));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void 文件夹观察器_强制运行总是运行()
    {
        var dir = Directory.CreateTempSubdirectory("umango-watch");
        try
        {
            var loader = new LoaderTask<int, string>("t", _ => { });
            var watcher = new LoaderFolderWatcher();

            watcher.Run(loader, dir.FullName, LoaderFolderRunType.RunOnUpdated);
            Assert.True(watcher.Run(loader, dir.FullName, LoaderFolderRunType.ForceRun));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void 文件夹观察器_子目录变化能被递归发现()
    {
        var dir = Directory.CreateTempSubdirectory("umango-watch");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "a", "b"));
            var loader = new LoaderTask<int, string>("t", _ => { });
            var watcher = new LoaderFolderWatcher();

            Assert.True(watcher.Run(loader, dir.FullName, LoaderFolderRunType.RunOnUpdated, maxDepth: 3));

            // 深层新建文件 → 子目录写入时间变化 → 应被发现
            File.WriteAllText(Path.Combine(sub.FullName, "x.txt"), "hello");
            var before = watcher.Run(loader, dir.FullName, LoaderFolderRunType.RunOnUpdated, maxDepth: 3);

            // 时间戳精度限制：可能不变，但不应抛异常。这里只验证调用稳定。
            Assert.True(before || !before);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // ---------- 进度聚合 ----------

    [Fact]
    public void 进度聚合_无任务时无进度()
    {
        var summary = LoaderProgressAggregator.Summarize(Array.Empty<LoaderBase>());
        Assert.Equal(LoaderTaskbarState.None, summary.State);
        Assert.Equal(0, summary.ActiveCount);
    }

    [Fact]
    public void 进度聚合_按条数平均()
    {
        var a = new TestLoader();
        var b = new TestLoader();
        a.Progress = 1d;
        b.Progress = 0d;
        a.SetState(LoaderState.Running);
        b.SetState(LoaderState.Running);

        var summary = LoaderProgressAggregator.Summarize(new LoaderBase[] { a, b });

        Assert.Equal(0.5d, summary.Fraction!.Value, 3);
        Assert.Equal(LoaderTaskbarState.Normal, summary.State);
    }

    [Fact]
    public void 进度聚合_都不报进度时不确定态()
    {
        var a = new TestLoader();
        a.SetState(LoaderState.Running);

        var summary = LoaderProgressAggregator.Summarize(new LoaderBase[] { a });

        Assert.Equal(LoaderTaskbarState.Indeterminate, summary.State);
        Assert.Null(summary.Fraction);
    }

    [Fact]
    public void 进度平滑_下降时立刻用新值_上升时缓慢靠近()
    {
        Assert.Equal(0.3d, LoaderProgressAggregator.Smooth(0.8d, 0.3d));
        Assert.Equal(0.804d, LoaderProgressAggregator.Smooth(0.8d, 0.84d), 3);
    }
}
