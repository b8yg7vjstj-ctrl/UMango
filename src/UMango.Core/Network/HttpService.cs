using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace UMango.Core.Network;

/// <summary>统一 HTTP 服务：代理/超时/UA/JSON 解析。单例 HttpClient，代理变更时重建。</summary>
public sealed class HttpService : IHttpService
{
    private readonly object _lock = new();
    private HttpClient? _client;
    private string? _proxyUrl;
    private string _userAgent = "UMango/0.1";

    public string? ProxyUrl { get => _proxyUrl; set { lock (_lock) { _proxyUrl = value; _client = null; } } }

    /// <summary>
    /// 绑定的代理管理器。为 null 时按 <see cref="ProxyUrl"/> 的老方式工作。
    /// 绑上之后，代理模式（直连/系统/自定义）与凭据都交给它决定。
    /// </summary>
    public ProxyManager? ProxyManager { get; set; }

    /// <summary>按当前代理配置重建底层客户端（换代理后要调用）。</summary>
    public void RefreshProxy() { lock (_lock) { _client = null; } }
    public int TimeoutMs { get; set; } = 30_000;
    public string UserAgent { get => _userAgent; set { lock (_lock) { _userAgent = value; _client = null; } } }

    private HttpClient GetClient()
    {
        lock (_lock)
        {
            if (_client is not null) return _client;
            var handler = new HttpClientHandler();
            var manager = ProxyManager;
            if (manager is not null)
            {
                // 交给代理管理器决定：它知道当前是直连、系统代理还是自定义代理，
                // 也知道要不要带凭据、要不要绕过本地地址。
                handler.Proxy = manager;
                handler.UseProxy = manager.Mode != ProxyMode.NoProxy;
                if (manager.Mode == ProxyMode.CustomProxy && manager.Credentials is not null)
                    handler.Credentials = manager.Credentials;
            }
            else if (!string.IsNullOrWhiteSpace(_proxyUrl))
            {
                handler.Proxy = new WebProxy(_proxyUrl);
                handler.UseProxy = true;
            }
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            return _client;
        }
    }

    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

    public async Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await GetClient().GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return new HttpResponse<T> { IsSuccess = true, Value = JsonSerializer.Deserialize<T>(json), StatusCode = (int)response.StatusCode };
                }
                if (attempt < MaxRetries && (int)response.StatusCode >= 500)
                {
                    await Task.Delay(RetryDelayMs * (attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }
                return new HttpResponse<T> { IsSuccess = false, StatusCode = (int)response.StatusCode, Error = response.ReasonPhrase };
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelayMs * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (Exception ex) { return new HttpResponse<T> { IsSuccess = false, Error = ex.Message }; }
        }
        return new HttpResponse<T> { IsSuccess = false, Error = "重试次数耗尽" };
    }

    public async Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var response = await GetClient().GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new HttpResponse<string> { IsSuccess = false, StatusCode = (int)response.StatusCode, Error = response.ReasonPhrase };
            return new HttpResponse<string> { IsSuccess = true, Value = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), StatusCode = (int)response.StatusCode };
        }
        catch (Exception ex) { return new HttpResponse<string> { IsSuccess = false, Error = ex.Message }; }
    }
}
