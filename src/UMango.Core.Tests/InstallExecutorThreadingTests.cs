using System.Collections.Specialized;
using UMango.Core.App.Tasks;
using UMango.Core.Execution;
using UMango.Core.Install;
using UMango.Core.PackageManagers;
using UMango.Core.Records;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 安装执行器的**线程亲和性**回归测试。
///
/// 背景：InstallExecutor 会修改 TaskCenter.Tasks 与 TaskModel.Children，
/// 它们是直接绑定 WPF 界面的 ObservableCollection，**只能在 UI 线程改**。
/// 若在这些 await 后加上 ConfigureAwait(false)，后续代码就会跑在线程池线程上，
/// 真机抛 "This type of CollectionView does not support changes to its
/// SourceCollection from a different thread" —— 编译与普通单测都发现不了。
///
/// ⚠️ 写这个测试时踩到的坑（值得记下来）：
/// 第一版用 Task.FromResult 做假依赖，结果**注入回归也照样通过** ——
/// 因为同步完成的任务根本不会真正让出，await 之后的代码仍在原线程执行，
/// ConfigureAwait(false) 形同虚设。所以这里的所有假依赖**必须真正异步**（await Task.Delay），
/// 否则测试就是"看起来很严、实际零覆盖"。
/// 已验证：把 ConfigureAwait(false) 注入到执行器里，本用例会失败。
/// </summary>
public class InstallExecutorThreadingTests
{
    /// <summary>
    /// **真正的消息泵**同步上下文：Post 只入队，由专用的"UI 线程"循环取出执行。
    ///
    /// 这一点是本用例的关键，也是前两版失败的原因：
    ///   · 第一版用 Task.FromResult 作依赖 → 任务同步完成，await 不让出，测不出任何东西；
    ///   · 第二版让 Post 内联执行 → 续体在"完成它的那个线程"上跑（往往是线程池线程），
    ///     并没有真正编组到 UI 线程，同样测不出东西。
    /// 只有"入队 + 专用线程泵出"才能真实模拟 WPF 的 UI 线程：
    /// 保留同步上下文 → 续体回到该专用线程；被 ConfigureAwait(false) 打断 → 落到线程池。
    /// </summary>
    private sealed class PumpSyncContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>在本线程上泵消息，直到 <paramref name="done"/> 返回 true，并收尾清空队列。</summary>
        public void RunUntil(Func<bool> done, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!done() && Environment.TickCount64 < deadline)
            {
                if (_queue.TryTake(out var item, 15)) item.Item1(item.Item2);
            }
            while (_queue.TryTake(out var item, 15)) item.Item1(item.Item2);
        }
    }

    /// <summary>**真正异步**的假适配器：PlanInstallAsync 会 await，从而产生真实的续体调度。</summary>
    private sealed class AsyncPlanAdapter : IPackageManager
    {
        public PackageSourceKind Kind => PackageSourceKind.Npm;
        public string DisplayName => "npm";
        public string InstallDirectory => "/tmp/fake";
        public string CliName => "npm";
        public IReadOnlyList<UMango.Core.Platform.PlatformKind> SupportedPlatforms
            => [UMango.Core.Platform.PlatformKind.Windows, UMango.Core.Platform.PlatformKind.MacOs,
                UMango.Core.Platform.PlatformKind.Linux];
        public SourceCapabilities Capabilities => new(true, true, true, false, true, true, true);
        public string InstallHint => "fake";
        public string? VerificationNote => null;

        public Task<SourceAvailability> ProbeAsync(CancellationToken ct = default)
            => Task.FromResult(new SourceAvailability { Kind = Kind, State = AvailabilityState.Available });

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string q, int l, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageInfo>>([]);

        public Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(string n, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageVersion>>([]);

        public Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(string n, string? v, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DependencyRef>>([]);

        public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstalledPackage>>([]);

        // ★ 关键：真正异步，让 await 产生真实让出
        public async Task<IReadOnlyList<InstallStep>> PlanInstallAsync(string n, string? v, CancellationToken ct = default)
        {
            await Task.Delay(1, ct);
            return
            [
                new InstallStep { Title = "依赖解析", Method = DownloadMethod.DependencyResolve, Weight = 0.5 },
                new InstallStep { Title = "下载", Method = DownloadMethod.RegistryTarball, Weight = 0.5 },
            ];
        }
    }

    [Fact]
    public void 集合修改发生在UI线程上_而不是线程池线程()
    {
        var registry = new PackageManagerRegistry([new AsyncPlanAdapter()]);
        var store = new InstallRecordStore(Path.Combine(Path.GetTempPath(), $"umango-thr-{Guid.NewGuid():N}.json"));

        var mutationThreads = new List<int>();
        var ctx = new PumpSyncContext();
        var uiThreadId = 0;
        var finished = false;
        TaskModel? installed = null;
        InstallResult? outcome = null;
        Exception? failure = null;

        // 真正的"UI 线程"：带消息泵的专用线程
        var uiThread = new Thread(() =>
        {
            uiThreadId = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(ctx);

            // 观测两类集合变更：
            //   1) TaskCenter.Tasks —— Register 时（在任何 await 之前，必在 UI 线程）
            //   2) task.Children   —— **这才是真正的风险点**：它在 await PlanAsync 之后才被填充，
            //      而 WPF 对"集合变更"不会自动编组到 UI 线程（属性变更会），
            //      所以一旦 PlanAsync 那处 await 带上 ConfigureAwait(false)，这里就会在线程池线程上改集合。
            void OnChildrenChanged(object? s, NotifyCollectionChangedEventArgs e)
                => mutationThreads.Add(Environment.CurrentManagedThreadId);
            void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
            {
                mutationThreads.Add(Environment.CurrentManagedThreadId);
                if (e.NewItems is not null)
                    foreach (var item in e.NewItems)
                        if (item is TaskModel tm) tm.Children.CollectionChanged += OnChildrenChanged;
            }
            TaskCenter.Tasks.CollectionChanged += OnChanged;

            try
            {
                var t = executorFactory(registry, store)
                    .InstallAsync(new InstallRequest(PackageSourceKind.Npm, "thread-probe"));
                t.ContinueWith(_ => finished = true, TaskScheduler.Default);
                ctx.RunUntil(() => finished, 10_000);

                if (t.IsCompletedSuccessfully)
                {
                    installed = t.Result.Task;
                    outcome = t.Result.Result;
                }
                else failure = t.Exception;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                TaskCenter.Tasks.CollectionChanged -= OnChanged;
                if (installed is not null) installed.Children.CollectionChanged -= OnChildrenChanged;
            }
        })
        { IsBackground = true };

        uiThread.Start();
        Assert.True(uiThread.Join(TimeSpan.FromSeconds(20)), "UI 线程未在超时内结束");

        Assert.Null(failure);
        Assert.True(finished, "安装未在超时内完成");
        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);

        // ★ 核心断言：所有集合修改都发生在那个 UI 线程上
        Assert.NotEmpty(mutationThreads);
        Assert.All(mutationThreads, id => Assert.Equal(uiThreadId, id));

        if (installed is not null) TaskCenter.Remove(installed);
    }

    /// <summary>执行器工厂：假运行器必须真正异步（否则 await 不让出，测不出线程编组）。</summary>
    private static InstallExecutor executorFactory(PackageManagerRegistry registry, InstallRecordStore store)
        => new(registry, store, async (_, onLine, ct) =>
        {
            await Task.Delay(1, ct);
            onLine?.Invoke(false, "downloading 50%");
            await Task.Delay(1, ct);
            onLine?.Invoke(false, "added 1 package");
            return new ProcessOutput { ExitCode = 0, DurationMs = 2 };
        });

    [Fact]
    public async Task 无线程上下文时也能正常完成_不依赖UI()
    {
        var registry = new PackageManagerRegistry([new AsyncPlanAdapter()]);
        var store = new InstallRecordStore(Path.Combine(Path.GetTempPath(), $"umango-thr2-{Guid.NewGuid():N}.json"));
        var executor = new InstallExecutor(registry, store, async (_, _, ct) =>
        {
            await Task.Delay(1, ct);
            return new ProcessOutput { ExitCode = 0, DurationMs = 1 };
        });

        var (task, result) = await executor.InstallAsync(new InstallRequest(PackageSourceKind.Npm, "no-ctx"));
        Assert.True(result.Success);
        Assert.Equal(TaskState.Success, task.State);
        TaskCenter.Remove(task);
    }
}
