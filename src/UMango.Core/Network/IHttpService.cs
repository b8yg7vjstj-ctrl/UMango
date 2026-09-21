namespace UMango.Core.Network;

/// <summary>HTTP 服务契约。统一代理、超时、UA、JSON 解析。</summary>
public interface IHttpService
{
    string? ProxyUrl { get; set; }
    int TimeoutMs { get; set; }
    string UserAgent { get; set; }
    Task<HttpResponse<T>> GetJsonAsync<T>(string url, CancellationToken ct = default);
    Task<HttpResponse<string>> GetStringAsync(string url, CancellationToken ct = default);
}

public sealed class HttpResponse<T> { public bool IsSuccess; public T? Value; public string? Error; public int StatusCode; }
public sealed class HttpResponse { public bool IsSuccess; public string? Error; public int StatusCode; }
