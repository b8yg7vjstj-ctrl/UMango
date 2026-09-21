namespace UMango.Core.Threading;

/// <summary>
/// 防抖：连续触发时只执行最后一次。
///
/// 最直接的使用场景：搜索框的输入——用户每敲一个键都会触发搜索，
/// 但只有"停止敲击后的最后一个键"才应该真正发起 CLI 探测，
/// 否则会短时间内起 5 个 npm 进程（前 4 个的结果会被丢弃）。
///
/// 与 PCL-CE 的 `PCL.Core/Utils/Threading/AsyncDebounce` 对应（Apache-2.0）。
/// PCL 那边是 30 行的实现；这里逻辑等价，结构更紧凑。
/// </summary>
public sealed class AsyncDebounce
{
    private CancellationTokenSource? _cts;
    private readonly int _delayMs;

    public AsyncDebounce(int delayMs = 300) => _delayMs = Math.Max(1, delayMs);

    /// <summary>
    /// 触发一次。若在 delayMs 内又触发，前一次会被取消。
    /// </summary>
    public async Task InvokeAsync(Func<Task> action, CancellationToken ct = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Task.Delay(_delayMs, _cts.Token).ConfigureAwait(false);
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 前一次的预期行为：不执行 */ }
    }

    public void Cancel() => _cts?.Cancel();
}
