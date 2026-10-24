// SPDX-License-Identifier: Apache-2.0
//
// RPC 的**客户端**侧：命令行工具/脚本用来与已在运行的 UMango 通信。
//
// 参考 PCL-CE 的 `PCL.Core/App/Essentials/RpcService.cs`（Apache-2.0）。
// PCL 只有服务端，客户端是外部工具；UMango 自己带一个客户端，
// 这样 `umango rpc install npm express` 这类命令不需要外部脚本。
//
// ⚠ 安全前提：命名管道**没有鉴权** —— 同机任何进程都能连上并读到响应。
// 客户端这边不做任何「安全」的粉饰：它只是把本地请求发出去。

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Ipc.Rpc;

/// <summary>RPC 客户端的响应。</summary>
public sealed class RpcClientResult
{
    /// <summary>创建结果。</summary>
    public RpcClientResult(RpcResponseStatus status, RpcResponseType type, string? content, string? name)
    {
        Status = status;
        Type = type;
        Content = content;
        Name = name;
    }

    /// <summary>状态。</summary>
    public RpcResponseStatus Status { get; }

    /// <summary>内容类型。</summary>
    public RpcResponseType Type { get; }

    /// <summary>内容。</summary>
    public string? Content { get; }

    /// <summary>名称。</summary>
    public string? Name { get; }

    /// <summary>是否成功。</summary>
    public bool IsSuccess => Status == RpcResponseStatus.Success;
}

/// <summary>命名管道 RPC 客户端。</summary>
public static class RpcClient
{
    /// <summary>默认连接超时。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 发送一次调用。
    /// </summary>
    /// <param name="pipeName">管道名。</param>
    /// <param name="request">请求（动词 + 目标）。</param>
    /// <param name="content">请求体（可空）。</param>
    /// <param name="timeout">连接超时。</param>
    public static async Task<RpcClientResult> SendAsync(
        RpcRequest request,
        string pipeName,
        string? content = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);

        // **重试连接**：服务端的管道实例是循环里反复创建/销毁的 ——
        // 处理完一个会话就 Dispose 掉旧实例，再建一个新的同���实例。
        // 在 macOS/Linux 上命名管道映射成 Unix domain socket，
        // 销毁与重建之间有一个**文件不存在的窗口**，
        // 客户端正好落在窗口里就会连不上（表现为随机失败）。
        // .NET 的 ConnectAsync 在 socket 文件不存在时不会等，而是直接失败，
        // 所以这里自己轮询重试。
        await ConnectWithRetryAsync(client, cts.Token).ConfigureAwait(false);

        using var writer = new StreamWriter(client, PipeProtocol.Encoding, 4096, leaveOpen: true);
        using var reader = new StreamReader(client, PipeProtocol.Encoding, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);

        // 协议第一行：客户端自报进程号。
        await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);

        var verb = request.Type switch
        {
            RpcRequestType.Get => "GET",
            RpcRequestType.Set => "SET",
            _ => "REQ",
        };
        await writer.WriteLineAsync($"{verb} {request.Target}").ConfigureAwait(false);
        await writer.WriteLineAsync(content ?? "").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var head = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(head))
            return new RpcClientResult(RpcResponseStatus.Error, RpcResponseType.Text, "服务端没有返回任何内容。", null);

        var parts = head.Split(' ', 3);
        var status = ParseStatus(parts.Length > 0 ? parts[0] : null);
        var type = parts.Length > 1 ? ParseType(parts[1]) : RpcResponseType.Text;
        var name = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;

        // 内容行：只有非 Empty 类型才有。
        string? body = null;
        if (type != RpcResponseType.Empty)
        {
            body = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }

        return new RpcClientResult(status, type, body, name);
    }

    /// <summary>
    /// 带重试地连接管道。
    ///
    /// 轮询间隔 20ms —— 管道重建窗口极短（微秒级），
    /// 但服务端刚启动时可能要几十毫秒才把管道建出来。
    /// </summary>
    private static async Task ConnectWithRetryAsync(NamedPipeClientStream client, CancellationToken ct)
    {
        const int MaxAttempts = 250; // 20ms × 250 ≈ 5s
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) when (attempt < MaxAttempts - 1)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaxAttempts - 1)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 解析响应状态。
    ///
    /// 协议里写的是大写的 <c>SUCCESS</c>/<c>FAILURE</c>/<c>ERROR</c>，
    /// 枚举成员是 <c>Success</c>/<c>Failure</c>/<c>Error</c> ——
    /// 忽略大小写的 <c>Enum.TryParse</c> 本来能匹配，但**协议允许扩展状态名**，
    /// 而 TryParse 遇到不认识的值会静默抛 <see cref="OverflowException"/>
    /// （对未知数字）或返回 false。用显式 switch 可以把未知值一律当 Error，
    /// 不会炸。
    /// </summary>
    private static RpcResponseStatus ParseStatus(string? text) => text?.ToUpperInvariant() switch
    {
        "SUCCESS" => RpcResponseStatus.Success,
        "FAILURE" => RpcResponseStatus.Failure,
        "ERROR" => RpcResponseStatus.Error,
        _ => RpcResponseStatus.Error,
    };

    /// <summary>解析响应内容类型（协议里是小写的 empty/text/json/base64）。</summary>
    private static RpcResponseType ParseType(string? text) => text?.ToLowerInvariant() switch
    {
        "empty" => RpcResponseType.Empty,
        "text" => RpcResponseType.Text,
        "json" => RpcResponseType.Json,
        "base64" => RpcResponseType.Base64,
        _ => RpcResponseType.Text,
    };

    /// <summary>便捷方法：调用一个函数。</summary>
    public static Task<RpcClientResult> InvokeAsync(
        string pipeName, string function, string? argument = null, string? content = null, TimeSpan? timeout = null) =>
        SendAsync(new RpcRequest(RpcRequestType.Invoke, function, content), pipeName, content, timeout);

    /// <summary>便捷方法：读一个属性。</summary>
    public static Task<RpcClientResult> GetAsync(string pipeName, string property, TimeSpan? timeout = null) =>
        SendAsync(new RpcRequest(RpcRequestType.Get, property, null), pipeName, null, timeout);

    /// <summary>便捷方法：写一个属性。</summary>
    public static Task<RpcClientResult> SetAsync(string pipeName, string property, string value, TimeSpan? timeout = null) =>
        SendAsync(new RpcRequest(RpcRequestType.Set, property, value), pipeName, value, timeout);
}
