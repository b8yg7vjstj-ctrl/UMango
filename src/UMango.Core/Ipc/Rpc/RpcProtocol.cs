// SPDX-License-Identifier: Apache-2.0
//
// 命名管道 RPC 的**协议与数据模型**。
//
// 参考 PCL-CE 的 `PCL.Core/App/Essentials/RpcService.cs`（276 行）、
// `RpcFunction.cs`、`RpcProperty.cs`、`RpcResponse.cs`（Apache-2.0，允许复用）。
// 采用时做的改动：
//   1. **不引入 PCL 的 IoC 依赖收集**（`[RegisterRpc]` / `DependencyCollector<>`）。
//      PCL 靠 IoC 容器扫描特性来收集函数与属性；UMango 的 Core 是纯 .NET、
//      不带 IoC 框架，所以改成**显式注册**（`RpcService.AddFunction/AddProperty`）——
//      显式注册更安全：能被远程调到的东西一定是有人手写注册过的。
//   2. PCL 的响应只写 `STATUS type [name]` 一行 + 可选内容行；这里保留同样的形状，
//      但**请求解析改成行协议**（与 UMango 现有的 `PipeServer` 一致），
//      而不是 PCL 的「读字符直到 ESC(27) 终止符」——
//      后者在客户端没写终止符时会一直阻塞，服务端只能靠超时兜底。
//   3. PCL 的属性名/函数名查找**转小写**；这里同样忽略大小写，
//      但用 `StringComparer.OrdinalIgnoreCase` 而不是 `ToLowerInvariant()`，
//      免得土耳其语 I 之类的 locale 问题（虽然实际很少见）。
//   4. PCL 的 `RpcProperty.SetValue` 用 `ref bool success` 出参，调用方必须小心；
//      这里改成**返回 bool**，语义一样但不容易用错。
//
// 安全说明（与 PCL 的差异，需在文档里写明）：
//   命名管道**没有鉴权** —— 同一台机器上任何进程都能连上来。
//   PCL 的 RPC 也没有鉴权（它靠"能拿到管道名"作为事实上的门槛）。
//   UMango 的对策是把门槛做成显式的：
//     · 管道名带进程号（`BuildPipeName`），但**不当作安全边界**；
//     · 函数与属性必须**显式注册**才能被调到；
//     · 提供 `RpcAuthorizationPolicy` 钩子，由 App 层决定「这次调用放不放行」
//       （例如按调用方进程号白名单）。默认策略是**全部拒绝**，
//       免得「忘了配策略 = 默认对外开放」。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UMango.Core.Ipc.Rpc;

/// <summary>RPC 请求类型。</summary>
public enum RpcRequestType
{
    /// <summary>读属性。</summary>
    Get = 0,

    /// <summary>写属性。</summary>
    Set = 1,

    /// <summary>调函数。</summary>
    Invoke = 2,
}

/// <summary>RPC 响应状态。</summary>
public enum RpcResponseStatus
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>失败（请求本身合法，但操作没做成）。</summary>
    Failure = 1,

    /// <summary>出错（请求不合法或内部异常）。</summary>
    Error = 2,
}

/// <summary>RPC 响应内容类型。</summary>
public enum RpcResponseType
{
    /// <summary>无内容。</summary>
    Empty = 0,

    /// <summary>纯文本。</summary>
    Text = 1,

    /// <summary>JSON。</summary>
    Json = 2,

    /// <summary>Base64 二进制。</summary>
    Base64 = 3,
}

/// <summary>
/// RPC 函数。返回响应内容。
/// </summary>
/// <param name="argument">命令行里的参数（函数名之后的空白分隔部分）。</param>
/// <param name="content">请求体（内容行）。</param>
/// <param name="indent">是否需要缩进输出（PCL 里用于日志/多行文本）。</param>
public delegate RpcResponse RpcFunction(string? argument, string? content, bool indent);

/// <summary>RPC 调用的授权判定。</summary>
/// <param name="name">被调用的函数或属性名。</param>
/// <param name="isProperty">true 表示是属性（读或写），false 表示是函数。</param>
/// <param name="clientProcessId">调用方自报的进程号（**不可信**）。</param>
/// <returns>true 表示放行。</returns>
public delegate bool RpcAuthorizationPolicy(string name, bool isProperty, int clientProcessId);

/// <summary>
/// 默认的授权策略：**一律拒绝**。
///
/// 为什么默认是拒绝而不是放行：
/// 命名管道没有鉴权，默认放行等于「本机任何进程都能调 UMango 的任何已注册能力」。
/// 想开放某个能力就显式注册它 —— 显式注册 + 默认拒绝，是两道独立的手续，
/// 少一道都会变成「忘了配 = 全开」。
/// </summary>
public static class RpcAuthorizationPolicies
{
    /// <summary>全部拒绝。</summary>
    public static bool DenyAll(string name, bool isProperty, int clientProcessId) => false;

    /// <summary>全部放行（⚠️ 只应在明确知道后果时使用）。</summary>
    public static bool AllowAll(string name, bool isProperty, int clientProcessId) => true;
}

/// <summary>一次 RPC 请求。</summary>
public sealed class RpcRequest
{
    /// <summary>创建请求。</summary>
    /// <param name="type">请求类型。</param>
    /// <param name="target">目标名（属性名或函数名，可带参数）。</param>
    /// <param name="content">请求体（可能为 null）。</param>
    public RpcRequest(RpcRequestType type, string target, string? content)
    {
        Type = type;
        Target = target ?? "";
        Content = content;
    }

    /// <summary>请求类型。</summary>
    public RpcRequestType Type { get; }

    /// <summary>目标（属性名 / 函数名[ 参数]）。</summary>
    public string Target { get; }

    /// <summary>请求体。</summary>
    public string? Content { get; }

    /// <summary>把请求行解析出来（协议首行）。</summary>
    /// <param name="header">请求行，形如 <c>GET name</c> / <c>REQ func arg</c>。</param>
    /// <exception cref="RpcProtocolException">格式不对时抛出。</exception>
    public static RpcRequest ParseHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            throw new RpcProtocolException("请求行不能为空。");

        var parts = header.Split(' ', 2);
        var verb = parts[0].ToUpperInvariant();
        if (parts.Length < 2 || parts[1].Length == 0)
            throw new RpcProtocolException("请求参数过少。");

        return verb switch
        {
            "GET" => new RpcRequest(RpcRequestType.Get, parts[1], null),
            "SET" => new RpcRequest(RpcRequestType.Set, parts[1], null),
            "REQ" => new RpcRequest(RpcRequestType.Invoke, parts[1], null),
            _ => throw new RpcProtocolException($"请求类型必须是 GET/SET/REQ 之一（收到 {verb}）。"),
        };
    }
}

/// <summary>协议层错误（请求本身就不合法）。</summary>
public class RpcProtocolException : Exception
{
    /// <summary>创建异常。</summary>
    public RpcProtocolException(string reason) : base(reason)
    {
        Reason = reason;
    }

    /// <summary>错误原因（会回给调用方）。</summary>
    public string Reason { get; }
}

/// <summary>属性操作失败（属性存在，但读或写被拒绝）。</summary>
public class RpcPropertyOperationFailedException : Exception
{
    /// <summary>创建异常。</summary>
    public RpcPropertyOperationFailedException() : base("属性操作被拒绝。")
    {
    }
}

/// <summary>RPC 响应。</summary>
public sealed class RpcResponse
{
    /// <summary>创建响应。</summary>
    /// <param name="status">状态。</param>
    /// <param name="type">内容类型。</param>
    /// <param name="content">内容。</param>
    /// <param name="name">名称（回显目标名，方便调用方对账）。</param>
    public RpcResponse(RpcResponseStatus status, RpcResponseType type = RpcResponseType.Empty, string? content = null, string? name = null)
    {
        if (content is not null && type == RpcResponseType.Empty)
            throw new ArgumentException("Empty 类型的响应不能带内容。", nameof(content));

        Status = status;
        Type = type;
        Content = content;
        Name = name;
    }

    /// <summary>状态。</summary>
    public RpcResponseStatus Status { get; }

    /// <summary>内容类型。</summary>
    public RpcResponseType Type { get; }

    /// <summary>名称。</summary>
    public string? Name { get; }

    /// <summary>内容。</summary>
    public string? Content { get; }

    /// <summary>空的成功响应。</summary>
    public static RpcResponse EmptySuccess { get; } = new(RpcResponseStatus.Success);

    /// <summary>空的失败响应。</summary>
    public static RpcResponse EmptyFailure { get; } = new(RpcResponseStatus.Failure);

    /// <summary>错误响应。</summary>
    public static RpcResponse Error(string content, string? name = null) =>
        new(RpcResponseStatus.Error, RpcResponseType.Text, content, name);

    /// <summary>成功的文本响应。</summary>
    public static RpcResponse Success(string content, string? name = null) =>
        new(RpcResponseStatus.Success, RpcResponseType.Text, content, name);

    /// <summary>成功的 JSON 响应。</summary>
    public static RpcResponse Json(string content, string? name = null) =>
        new(RpcResponseStatus.Success, RpcResponseType.Json, content, name);

    /// <summary>
    /// 把响应写进流。
    ///
    /// 格式（与 PCL 一致）：
    /// <code>
    /// STATUS type [name]
    /// [content]
    /// </code>
    /// </summary>
    public void WriteTo(StreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var nameArea = Name is null ? "" : " " + Name;
        writer.WriteLine($"{Status.ToString().ToUpperInvariant()} {Type.ToString().ToLowerInvariant()}{nameArea}");
        if (Content is not null)
            writer.WriteLine(Content);
    }

    /// <summary>把响应转成字符串（便于测试与日志）。</summary>
    public override string ToString()
    {
        var nameArea = Name is null ? "" : " " + Name;
        var head = $"{Status.ToString().ToUpperInvariant()} {Type.ToString().ToLowerInvariant()}{nameArea}";
        return Content is null ? head : head + "\n" + Content;
    }
}

/// <summary>RPC 属性。</summary>
public sealed class RpcProperty
{
    /// <summary>创建属性。</summary>
    /// <param name="name">属性名。</param>
    /// <param name="onGetValue">读回调。</param>
    /// <param name="onSetValue">写回调；为 null 时属性只读。</param>
    public RpcProperty(string name, Func<string?> onGetValue, Action<string?>? onSetValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(onGetValue);

        Name = name;
        _onGetValue = onGetValue;
        _onSetValue = onSetValue;
    }

    private readonly Func<string?> _onGetValue;
    private readonly Action<string?>? _onSetValue;

    /// <summary>属性名。</summary>
    public string Name { get; }

    /// <summary>是否可写。</summary>
    public bool Settable => _onSetValue is not null;

    /// <summary>
    /// 读属性。
    /// </summary>
    /// <exception cref="RpcPropertyOperationFailedException">读被拒绝时。</exception>
    public string? Value
    {
        get
        {
            try
            {
                return _onGetValue();
            }
            catch (RpcPropertyOperationFailedException)
            {
                throw;
            }
            catch (Exception)
            {
                // 读回调抛的任何异常都归一成"操作失败"，
                // 不把内部异常原文透给远程调用方（那会泄露内部结构）。
                throw new RpcPropertyOperationFailedException();
            }
        }
    }

    /// <summary>
    /// 写属性。
    /// </summary>
    /// <exception cref="RpcPropertyOperationFailedException">写被拒绝或属性只读时。</exception>
    public void SetValue(string? value)
    {
        if (_onSetValue is null)
            throw new RpcPropertyOperationFailedException();

        try
        {
            _onSetValue(value);
        }
        catch (RpcPropertyOperationFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RpcPropertyOperationFailedException();
        }
    }
}
