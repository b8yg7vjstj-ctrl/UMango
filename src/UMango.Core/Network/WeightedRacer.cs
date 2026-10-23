using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Network;

/// <summary>
/// 加权竞速器：同时向多个候选发起同一请求，最快成功者赢，输家取消。
///
/// **来源与差异**（PCL-CE `PCL.Core/IO/Net/WeightedDnsRacerClient.cs`，Apache-2.0）：
/// 逐行迁语义，但去掉了三处不该照搬的地方：
/// <list type="bullet">
/// <item>原版用 <c>IDnsClient</c>/<c>DnsMessage</c>，依赖 <c>Ae.Dns.Protocol</c> 包。
/// UMango 不为 DNS 查询引入新依赖，改成泛型 + 函数式委托，
/// 任何"发请求拿结果"的场景都能用（DNS、包源镜像、更新源…）。</item>
/// <item>原版的权重只在获胜时 <c>+1</c>，上界 255 后整个竞速器停止更新权重
/// （<c>_updateWeigh = false</c>）。这是刻意的长期收敛：让最初快的候选长期占优。
/// 保留该语义，但补了"负反馈"：失败一次会扣权重，避免坏节点长期霸榜。</item>
/// <item>原版 <c>WhenAnySuccessAsync</c> 挂在 <c>PCL.Core.Utils.Exts</c> 上，
/// 这里就地实现，不跨模块依赖。</item>
/// </list>
///
/// **权重是什么**：候选的成功次数（0–255）。排序时权重高的先发、也更容易独占名额，
/// 但<em>所有被选中的候选仍然是并发发起</em> —— 权重只决定"谁被选中"，
/// 不决定"谁先发"，所以慢节点仍有机会赢。
/// </summary>
/// <typeparam name="TClient">候选客户端类型。</typeparam>
/// <typeparam name="TResult">竞速结果类型。</typeparam>
public sealed class WeightedRacer<TClient, TResult> : IDisposable where TClient : class
{
    private readonly (TClient Client, byte Weight)[] _clients;
    private readonly Func<TClient, CancellationToken, Task<TResult>> _race;
    private readonly int _concurrentCount;
    private readonly object _lock = new();
    private bool _updateWeight = true;
    private bool _disposed;

    /// <summary>竞速器名，仅用于日志。</summary>
    public string Name { get; }

    /// <summary>竞速结果：谁赢了、拿到的值、以及败者是否也在同一轮成功。</summary>
    public sealed record RaceResult(TClient Winner, TResult Value, IReadOnlyList<TClient> Losers);

    /// <summary>
    /// 创建竞速器。
    /// </summary>
    /// <param name="name">竞速器名。</param>
    /// <param name="concurrentCount">每轮实际参与的候选数。</param>
    /// <param name="race">单个候选的竞速动作。</param>
    /// <param name="clients">候选客户端；数量必须不少于 <paramref name="concurrentCount"/>。</param>
    public WeightedRacer(string name, int concurrentCount,
        Func<TClient, CancellationToken, Task<TResult>> race,
        params TClient[] clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(race);
        if (concurrentCount < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrentCount), concurrentCount, "每轮至少要有一个候选。");
        if (clients.Length < concurrentCount)
            throw new ArgumentException(
                $"候选只有 {clients.Length} 个，凑不齐一轮 {concurrentCount} 个并发。", nameof(clients));

        Name = name;
        _concurrentCount = concurrentCount;
        _race = race;
        _clients = clients.Select(static c => (Client: c, Weight: (byte)0)).ToArray();
    }

    /// <summary>当前各候选的权重快照（供界面显示"哪个源更快"）。</summary>
    public IReadOnlyList<(TClient Client, byte Weight)> Weights
    {
        get
        {
            lock (_lock) return _clients.Select(static x => (x.Client, x.Weight)).ToArray();
        }
    }

    /// <summary>
    /// 按权重选出本轮候选（权重降序，取前 N 个）。对外可见是为了让"谁被选中"可测 ——
    /// 否则只能靠观察调用次数间接推断。
    /// </summary>
    /// <returns>本轮实际参与的候选。</returns>
    public IReadOnlyList<TClient> SelectCandidates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Candidates().Select(static x => x.Client).ToArray();
    }

    /// <summary>按权重选出本轮候选：权重高的优先。</summary>
    private (TClient Client, byte Weight)[] Candidates()
    {
        lock (_lock)
        {
            return _clients
                .OrderByDescending(static x => x.Weight)
                .Take(_concurrentCount)
                .ToArray();
        }
    }

    /// <summary>跑一轮竞速，返回最快成功者。</summary>
    /// <param name="ct">外部取消。</param>
    /// <exception cref="InvalidOperationException">全部候选都失败时抛出。</exception>
    public async Task<RaceResult> RaceAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var candidates = Candidates();
        if (candidates.Length == 0) throw new InvalidOperationException($"{Name}：没有可用候选。");

        // 与外部取消联动：外部取消时立刻中止本轮所有候选，而不是等它们自己超时。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new Task<TResult>[candidates.Length];
        for (var i = 0; i < candidates.Length; i++) tasks[i] = RunOne(candidates[i].Client, cts.Token);

        var winnerTask = await WhenAnySuccessAsync(tasks, cts.Token).ConfigureAwait(false);
        if (winnerTask is null)
        {
            cts.Cancel();

            // 全军覆没时也要记账：否则坏节点一次失败都没被扣分，
            // 会一直留在候选池里，下一轮继续参与、继续占并发名额。
            foreach (var (client, _) in candidates) Score(client, success: false);
            throw new InvalidOperationException($"{Name}：全部候选均失败。");
        }

        var index = Array.IndexOf(tasks, winnerTask);
        var winner = candidates[index].Client;
        var value = await winnerTask.ConfigureAwait(false);

        Score(winner);
        foreach (var (client, _) in candidates)
            if (!ReferenceEquals(client, winner)) Score(client, success: false);

        // 通知输家：它们的结果已无人需要，尽早释放连接。
        cts.Cancel();

        var losers = candidates.Where(static (c, _) => true).Select(static c => c.Client)
            .Where(c => !ReferenceEquals(c, winner)).ToArray();
        return new RaceResult(winner, value, losers);
    }

    /// <summary>跑一个候选；异常在这里被吞掉，转成"失败"由 WhenAnySuccess 统一处理。</summary>
    private async Task<TResult> RunOne(TClient client, CancellationToken token)
    {
        try
        {
            return await _race(client, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不是失败（常是"已经有人赢了"），但也不能当成功往上抛。
            throw;
        }
        catch
        {
            // 其余异常统一折叠成失败：竞速的意义就是"总有一个能用"，
            // 单个候选的异常细节由各自的上层记录，不该中断整轮。
            throw new RacingCandidateFailedException(Name, client);
        }
    }

    /// <summary>记一次胜负：赢 +1，输 -1（不低于 0）。</summary>
    private void Score(TClient client, bool success = true)
    {
        lock (_lock)
        {
            for (var i = 0; i < _clients.Length; i++)
            {
                if (!ReferenceEquals(_clients[i].Client, client)) continue;

                if (_updateWeight)
                {
                    if (success)
                    {
                        if (_clients[i].Weight < byte.MaxValue)
                            _clients[i] = (client, (byte)(_clients[i].Weight + 1));
                        else
                            // 权重封顶后停止更新：让长期领先的候选稳定占优，
                            // 否则后起的偶然快一次会打断已经建立的优势。
                            _updateWeight = false;
                    }
                    else if (_clients[i].Weight > 0)
                    {
                        _clients[i] = (client, (byte)(_clients[i].Weight - 1));
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// 等第一个成功者；全部失败或外部取消时返回 <c>null</c>。
    ///
    /// <b>取消哨兵是关键</b>：只把候选任务丢进 <c>Task.WhenAny</c> 是不够的 ——
    /// 若某个候选不理会 <see cref="CancellationToken"/>（原生下载器回调、
    /// 某些 COM/互操作实现都会这样），它会永远不完成，<c>WhenAny</c> 就永远不返回，
    /// 外部取消形同虚设。所以必须把"取消本身"也当成一个参与竞速的任务。
    /// </summary>
    private static async Task<Task<TResult>?> WhenAnySuccessAsync(Task<TResult>[] tasks, CancellationToken token)
    {
        // 永不成功、只在取消时完成的哨兵。
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = sentinel.Task.ContinueWith(static _ => (Task<TResult>)null!,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        // 哨兵**不能**放进 pending：它只在取消时完成，若混进去，
        // 「候选全部失败」时集合永远不会变空，循环就成了死等。
        // 所以候选各自等，同时与哨兵单独赛一把。
        var pending = new HashSet<Task<Task<TResult>>>(tasks.Select(WhenAnySuccessWrapper));

        await using var registration = token.Register(static state =>
        {
            var tcs = (TaskCompletionSource)state!;
            tcs.TrySetResult();
        }, sentinel);

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending.Append(cancelled)).ConfigureAwait(false);
            if (ReferenceEquals(finished, cancelled)) return null;

            var typed = await finished.ConfigureAwait(false);
            if (typed.Status == TaskStatus.RanToCompletion) return typed;
            pending.Remove(finished);
        }
        return null;
    }

    /// <summary>把候选包一层：失败也返回自身（而非抛），便于统一在循环里判定。</summary>
    private static async Task<Task<TResult>> WhenAnySuccessWrapper(Task<TResult> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 失败详情不影响竞速判定：这一轮的结果由状态码决定。
        }
        return task;
    }

    /// <summary>单个候选失败。消息里带竞速器名与候选，便于排查是哪条链路断了。</summary>
    public sealed class RacingCandidateFailedException : Exception
    {
        public RacingCandidateFailedException(string racerName, object client)
            : base($"{racerName}：候选 {client} 失败。")
        {
            RacerName = racerName;
            Candidate = client;
        }

        /// <summary>竞速器名。</summary>
        public string RacerName { get; }

        /// <summary>失败的候选。</summary>
        public object Candidate { get; }
    }

    /// <summary>
    /// 释放竞速器。<b>不释放候选客户端</b> —— 它们通常由调用方共享
    /// （同一套 HTTP 连接可能要用于非竞速路径），由调用方负责生命周期。
    /// </summary>
    public void Dispose() => _disposed = true;
}
