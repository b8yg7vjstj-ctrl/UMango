using System.Diagnostics;
using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载任务门面：**统一的重试 + 镜像源降级**。
///
/// 这是 PCL-CE `Modules/Network/Facade/ModNet.cs` 与
/// `Modules/Network/Http/Requester.cs` 里"重试 + 换源"那一层的等价物。
///
/// 与原版的差异：
///   1. 原版把重试和换源塞在一个双层 `for` 循环里，且**换源后每个源都会重试满次数**
///      —— 主源失败 3 次 + 备用源失败 3 次 = 6 次请求。
///      这里默认**总量封顶**（<see cref="MaxTotalAttempts"/>），避免在网络完全不通时
///      把用户等很久（这是原版一个真实的体验问题）。
///   2. 原版只支持**一个** FallbackUrl；这里支持**任意多个**镜像源。
///   3. 原版是同步接口（内部 `.GetAwaiter().GetResult()` 阻塞 UI 线程）→ 这里是全异步。
///   4. 每次尝试都会**如实记进 <see cref="DownloadAttempt"/> 列表**，界面能显示
///      "主源失败 → 正在试镜像 A" 这样的过程，而不是只看到最终失败。
/// </summary>
public sealed class DownloadTaskFacade
{
    private readonly DownloadEngineRegistry _registry;

    public DownloadTaskFacade(DownloadEngineRegistry registry) => _registry = registry;

    /// <summary>
    /// 整个任务最多尝试多少次（跨所有源）。
    /// 默认 6 —— 足以"主源试 3 次 + 镜像试 3 次"，但不至于无限拖。
    /// </summary>
    public int MaxTotalAttempts { get; init; } = 6;

    /// <summary>
    /// 每次重试前的等待时长计算。默认用 <see cref="DownloadRetryPolicy.DelayFor"/>。
    ///
    /// **做成可注入**是为了让测试不必真的睡在指数退避上（否则断言"重试几次"
    /// 要等好几秒，测试一慢就容易掩盖真实问题）。生产代码不要改它。
    /// </summary>
    public Func<DownloadSource, int, TimeSpan>? DelayProvider { get; init; }

    /// <summary>
    /// 下载一个文件，按顺序尝试各来源。
    /// </summary>
    /// <param name="sources">候选来源，第一个是主源。</param>
    /// <param name="request">下载请求（URL 会被每个来源覆盖）。</param>
    /// <param name="bridge">终端桥，用于把过程回传到界面。</param>
    /// <param name="onProgress">进度回调。</param>
    /// <param name="onAttempt">每次尝试结束后的回调（用于界面显示"换源了"）。</param>
    /// <param name="policy">重试策略。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<DownloadTaskResult> DownloadAsync(
        IReadOnlyList<DownloadSource> sources,
        DownloadRequest request,
        TerminalBridge? bridge = null,
        Action<DownloadProgress>? onProgress = null,
        Action<DownloadAttempt>? onAttempt = null,
        DownloadRetryPolicy? policy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(request);

        policy ??= new DownloadRetryPolicy();

        var usable = sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (usable.Count == 0)
        {
            // 也要留一条尝试记录，否则调用方拿不到 ErrorCode。
            var noSource = new DownloadResult(false, null, "no_source", "没有配置任何下载来源", 0);
            var attempt = new DownloadAttempt(DownloadSource.Primary("(无)", "无来源"), 0, false, noSource, TimeSpan.Zero);
            return new DownloadTaskResult(false, null, [attempt], attempt);
        }

        var attempts = new List<DownloadAttempt>();
        var total = 0;

        foreach (var source in usable)
        {
            var attemptNo = 0;
            while (attemptNo < Math.Max(1, policy.AttemptsPerSource) && total < MaxTotalAttempts)
            {
                attemptNo++;
                total++;
                ct.ThrowIfCancellationRequested();

                var delay = DelayProvider?.Invoke(source, attemptNo) ?? policy.DelayFor(attemptNo);
                if (delay > TimeSpan.Zero)
                {
                    bridge?.Write(false, $"等待 {delay.TotalSeconds:0.#} 秒后重试（{source.Name}）");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                bridge?.Write(false, attemptNo == 1
                    ? $"下载来源：{source.Name}"
                    : $"重试 {source.Name}（第 {attemptNo} 次）");

                var sw = Stopwatch.StartNew();
                var effective = request with { Url = source.Url };
                var result = await _registry.DownloadAsync(effective, bridge, onProgress, ct).ConfigureAwait(false);
                sw.Stop();

                var attempt = new DownloadAttempt(source, attemptNo, result.Success, result, sw.Elapsed);
                attempts.Add(attempt);
                onAttempt?.Invoke(attempt);

                if (attempt.Success)
                {
                    return new DownloadTaskResult(true, result.OutputPath, attempts, attempt);
                }

                // 取消导致的失败不重试
                if (result.ErrorCode == "canceled") break;
            }
        }

        var last = attempts.Count > 0 ? attempts[^1] : null;
        return new DownloadTaskResult(false, null, attempts, last);
    }

    /// <summary>便捷重载：只给一个 URL，没有镜像。</summary>
    public Task<DownloadTaskResult> DownloadAsync(
        string url,
        DownloadRequest request,
        TerminalBridge? bridge = null,
        Action<DownloadProgress>? onProgress = null,
        Action<DownloadAttempt>? onAttempt = null,
        DownloadRetryPolicy? policy = null,
        CancellationToken ct = default)
        => DownloadAsync([DownloadSource.Primary(url)], request, bridge, onProgress, onAttempt, policy, ct);
}
