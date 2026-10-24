// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/Net/Http/HttpSenderExtension.cs 与 PCL.Core/IO/Net/NetworkService.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 命名空间 PCL.Core.* → UMango.Core.Network.Http。
//   2. **去掉 Polly 依赖**。原版用 Polly 的 Policy.Handle<HttpRequestException>()
//      + WaitAndRetryAsync 做重试；UMango 不引入第三个重试库，
//      退避算法（指数 + 上限）本身就是十几行，自己写更可控也更好测。
//   3. 去掉原版的 PCL 专属元数据头（PCL-Community/PCL2-CE UA 与 pcl2.server Referer）
//      —— 那是在向 PCL 官方服务器表明身份，UMango 不该发这种头。
//      UA 改为可配置，默认 UMango 自己的。
//   4. 原版用 LogWrapper 记日志；这里用一个回调把重试事件交给调用方，
//      由 App 层决定写不写日志（Core 不该绑死日志实现）。

using System.Net.Http;

namespace UMango.Core.Network.Http;

/// <summary>一次重试尝试的上下文。</summary>
public readonly record struct HttpRetryInfo(int Attempt, int MaxAttempts, TimeSpan Delay, Exception? Error, string? Url);

/// <summary>
/// 带重试的发送。
///
/// 重试策略：**只重试传输层失败**（HttpRequestException / 超时），
/// 不重试 4xx —— 服务端明确说了"这个请求不合法"，再试一百次也一样。
/// 5xx 才重试。
/// </summary>
public static class HttpSender
{
    private const int BaseRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 30_000;

    /// <summary>指数退避：1s, 2s, 4s … 封顶 30s。</summary>
    public static TimeSpan DefaultDelayProvider(int attempt)
    {
        var delayMs = Math.Pow(2, attempt - 1) * BaseRetryDelayMs;
        delayMs = Math.Min(delayMs, MaxRetryDelayMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>判断某个状态码是否值得重试。</summary>
    public static bool ShouldRetryStatus(int statusCode)
        => statusCode >= 500 && statusCode <= 599;

    /// <summary>
    /// 发送请求，失败时按退避策略重试。
    /// </summary>
    /// <param name="request">要发送的请求。内部会克隆，所以调用方可以保留它。</param>
    /// <param name="httpClient">使用的客户端；为 null 时必须由调用方提供</param>
    /// <param name="userAgent">要加的 User-Agent；为 null 或空则不加</param>
    /// <param name="retryTimes">最大重试次数（不含首次尝试）。0 表示不重试。</param>
    /// <param name="onRetry">每次重试前的回调（可为空）</param>
    /// <param name="delayProvider">退避时长计算（可为空，默认指数退避）</param>
    public static async Task<HttpResponseMessage> SendAsync(
        this HttpRequestMessage request,
        HttpClient httpClient,
        string? userAgent = null,
        int retryTimes = 3,
        Action<HttpRetryInfo>? onRetry = null,
        Func<int, TimeSpan>? delayProvider = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (retryTimes < 0) throw new ArgumentOutOfRangeException(nameof(retryTimes));

        delayProvider ??= DefaultDelayProvider;
        var maxAttempts = retryTimes + 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpRequestMessage? attemptRequest = null;
            try
            {
                attemptRequest = await request.CloneAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(userAgent))
                    attemptRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent);

                var response = await httpClient.SendAsync(attemptRequest, completionOption, ct).ConfigureAwait(false);

                if (attempt < maxAttempts && ShouldRetryStatus((int)response.StatusCode))
                {
                    var delay = delayProvider(attempt);
                    onRetry?.Invoke(new HttpRetryInfo(attempt, maxAttempts, delay, null, request.RequestUri?.ToString()));
                    response.Dispose();
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方主动取消 —— 不重试，直接往上抛。
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                lastError = ex;
                if (attempt >= maxAttempts) break;

                var delay = delayProvider(attempt);
                onRetry?.Invoke(new HttpRetryInfo(attempt, maxAttempts, delay, ex, request.RequestUri?.ToString()));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                attemptRequest?.Dispose();
            }
        }

        throw lastError ?? new HttpRequestException($"请求失败：{request.RequestUri}");
    }
}
