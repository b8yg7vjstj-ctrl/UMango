using UMango.Core.Terminal;

namespace UMango.Core.Download;

/// <summary>
/// 下载来源：一个"从哪个 URL 拿" 的候选项。
///
/// PCL-CE 的做法是 <c>RequestParam.FallbackUrl</c> —— 只能配**一个**备用地址，
/// 且重试与换源被塞在同一个双层循环里（<c>Requester.FetchAsync</c>）。
/// UMango 把它拆成两个正交概念：
///   · <see cref="DownloadSource"/> —— **有哪些地址可以试**（主源 + 若干镜像）；
///   · <see cref="DownloadRetryPolicy"/> —— **每个地址试几次、怎么退避**。
/// 这样"换源"和"重试"可以独立配置，也让界面能如实显示"正在试第几个源"。
/// </summary>
public sealed record DownloadSource(string Url, string Name)
{
    /// <summary>主源。</summary>
    public static DownloadSource Primary(string url, string name = "主源") => new(url, name);

    /// <summary>镜像源。</summary>
    public static DownloadSource Mirror(string url, string name) => new(url, name);
}

/// <summary>重试策略。</summary>
public sealed record DownloadRetryPolicy
{
    /// <summary>每个来源最多尝试几次（含首次）。至少 1。</summary>
    public int AttemptsPerSource { get; init; } = 3;

    /// <summary>第一次重试前的等待。</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>退避上限。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>不重试（只试一次每个源）。</summary>
    public static DownloadRetryPolicy None { get; } = new() { AttemptsPerSource = 1 };

    /// <summary>第 n 次尝试前的等待时长（n 从 1 开始）。指数退避 + 上限。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        var ms = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 2);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}

/// <summary>一次下载尝试的结果。</summary>
public sealed record DownloadAttempt(
    DownloadSource Source,
    int Attempt,
    bool Success,
    DownloadResult Result,
    TimeSpan Elapsed)
{
    /// <summary>这次失败的原因摘要。</summary>
    public string? FailureSummary =>
        Success ? null
        : Result.ErrorCode is { } code && Result.Message is { } msg ? $"{code}: {msg}"
        : Result.Message ?? Result.ErrorCode;
}

/// <summary>一次下载任务的完整结果。</summary>
public sealed record DownloadTaskResult(
    bool Success,
    string? OutputPath,
    IReadOnlyList<DownloadAttempt> Attempts,
    DownloadAttempt? LastAttempt)
{
    /// <summary>实际成功的来源名。</summary>
    public string? SucceededSourceName => LastAttempt is { Success: true } a ? a.Source.Name : null;

    /// <summary>最终错误码。</summary>
    public string? ErrorCode => LastAttempt?.Result.ErrorCode;

    /// <summary>最终错误信息。</summary>
    public string? ErrorMessage => LastAttempt?.Result.Message;
}
