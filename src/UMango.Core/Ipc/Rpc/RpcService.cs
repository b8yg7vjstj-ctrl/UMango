// SPDX-License-Identifier: Apache-2.0
//
// RPC 服务本体：注册表 + 请求分发。
//
// 参考 PCL-CE 的 `PCL.Core/App/Essentials/RpcService.cs`（Apache-2.0）。
// 与原版的差异见 `RpcProtocol.cs` 头部；核心区别是：
//   · **显式注册**取代 IoC 特性扫描（更安全：能远程调的一定是人手写注册过的）；
//   · **默认拒绝**的授权策略（命名管道没有鉴权，默认放行等于对外开放）；
//   · 分发逻辑抽成**可单测的纯函数**（`Dispatch`），不与管道 IO 纠缠。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UMango.Core.Ipc.Rpc;

/// <summary>RPC 函数注册表 + 请求分发。</summary>
public sealed class RpcRegistry
{
    private readonly Dictionary<string, RpcFunction> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RpcProperty> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>创建注册表。</summary>
    /// <param name="authorization">授权策略；为 null 时用**默认拒绝**。</param>
    public RpcRegistry(RpcAuthorizationPolicy? authorization = null)
    {
        Authorization = authorization ?? RpcAuthorizationPolicies.DenyAll;
    }

    /// <summary>授权策略。</summary>
    public RpcAuthorizationPolicy Authorization { get; set; }

    /// <summary>已注册的函数名。</summary>
    public IReadOnlyCollection<string> FunctionNames
    {
        get { lock (_lock) { return new List<string>(_functions.Keys); } }
    }

    /// <summary>已注册的属性名。</summary>
    public IReadOnlyCollection<string> PropertyNames
    {
        get { lock (_lock) { return new List<string>(_properties.Keys); } }
    }

    /// <summary>注册一个函数（同名则覆盖）。</summary>
    public bool AddFunction(string name, RpcFunction function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(function);

        lock (_lock)
        {
            _functions[name] = function;
        }
        return true;
    }

    /// <summary>移除一个函数。</summary>
    public bool RemoveFunction(string name)
    {
        lock (_lock)
        {
            return _functions.Remove(name);
        }
    }

    /// <summary>注册一个属性（同名则覆盖）。</summary>
    public bool AddProperty(RpcProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        lock (_lock)
        {
            _properties[property.Name] = property;
        }
        return true;
    }

    /// <summary>移除一个属性。</summary>
    public bool RemoveProperty(string name)
    {
        lock (_lock)
        {
            return _properties.Remove(name);
        }
    }

    /// <summary>取函数。</summary>
    public RpcFunction? FindFunction(string name)
    {
        lock (_lock)
        {
            return _functions.GetValueOrDefault(name);
        }
    }

    /// <summary>取属性。</summary>
    public RpcProperty? FindProperty(string name)
    {
        lock (_lock)
        {
            return _properties.GetValueOrDefault(name);
        }
    }

    /// <summary>
    /// 分发一次请求。
    ///
    /// **这一步不碰 IO**，所以能直接单测（用假函数/假属性验证行为）。
    /// 管道层只负责把行读进来、把响应写出去。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="clientProcessId">调用方自报的进程号（**不可信**，只作日志与策略输入）。</param>
    public RpcResponse Dispatch(RpcRequest request, int clientProcessId = 0)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.Type)
        {
            case RpcRequestType.Get:
                return DispatchGet(request.Target, clientProcessId);
            case RpcRequestType.Set:
                return DispatchSet(request.Target, request.Content, clientProcessId);
            case RpcRequestType.Invoke:
                return DispatchInvoke(request.Target, request.Content, clientProcessId);
            default:
                return RpcResponse.Error("未知的请求类型。");
        }
    }

    private RpcResponse DispatchGet(string target, int clientProcessId)
    {
        var name = target.Trim();
        var property = FindProperty(name);
        if (property is null)
            return RpcResponse.Error($"不存在属性 {name}", name);

        if (!Authorization(name, true, clientProcessId))
            return RpcResponse.Error($"属性 {name} 的读取被拒绝。", name);

        try
        {
            var value = property.Value;
            return new RpcResponse(RpcResponseStatus.Success, RpcResponseType.Text, value ?? "", name);
        }
        catch (RpcPropertyOperationFailedException)
        {
            return RpcResponse.EmptyFailure;
        }
    }

    private RpcResponse DispatchSet(string target, string? content, int clientProcessId)
    {
        var name = target.Trim();
        var property = FindProperty(name);
        if (property is null)
            return RpcResponse.Error($"不存在属性 {name}", name);

        if (!Authorization(name, true, clientProcessId))
            return RpcResponse.Error($"属性 {name} 的写入被拒绝。", name);

        if (!property.Settable)
            return RpcResponse.EmptyFailure;

        try
        {
            property.SetValue(content);
            return RpcResponse.EmptySuccess;
        }
        catch (RpcPropertyOperationFailedException)
        {
            return RpcResponse.EmptyFailure;
        }
    }

    private RpcResponse DispatchInvoke(string target, string? content, int clientProcessId)
    {
        // 函数名后面可以跟参数：'REQ install npm express'
        var parts = target.Split(' ', 2);
        var name = parts[0];
        var argument = parts.Length > 1 ? parts[1] : null;

        // 末位 '$' 表示「输出缩进」（PCL 的同名约定）。
        var indent = false;
        if (name.EndsWith('$'))
        {
            indent = true;
            name = name[..^1];
        }

        var function = FindFunction(name);
        if (function is null)
            return RpcResponse.Error($"不存在函数 {name}", name);

        if (!Authorization(name, false, clientProcessId))
            return RpcResponse.Error($"函数 {name} 的调用被拒绝。", name);

        try
        {
            return function(argument, content, indent);
        }
        catch (Exception ex)
        {
            return RpcResponse.Error(ex.Message, name);
        }
    }
}

/// <summary>
/// RPC 服务：把 <see cref="RpcRegistry"/> 接到命名管道上。
///
/// 协议（行协议，与 UMango 的 `PipeServer` 一致）：
/// <code>
/// [客户端进程号]        ← PipeServer 已经读掉了
/// GET/SET/REQ target   ← 请求行
/// [content]             ← 可选的请求体（下一行；没有则为空）
/// </code>
/// 响应：
/// <code>
/// STATUS type [name]
/// [content]
/// </code>
/// </summary>
public sealed class RpcService : IAsyncDisposable
{
    private readonly RpcRegistry _registry;
    private readonly PipeServer _server;
    private readonly Action<string>? _log;

    /// <summary>创建服务。</summary>
    /// <param name="pipeName">管道名（应短，见 <see cref="PipeServer.BuildPipeName"/>）。</param>
    /// <param name="registry">注册表；为 null 时新建一个（默认拒绝授权）。</param>
    /// <param name="log">日志回调。</param>
    public RpcService(string pipeName, RpcRegistry? registry = null, Action<string>? log = null)
    {
        _registry = registry ?? new RpcRegistry();
        _log = log;
        _server = new PipeServer(pipeName, HandleSessionAsync, ex => _log?.Invoke("会话出错：" + ex.Message));
    }

    /// <summary>注册表。</summary>
    public RpcRegistry Registry => _registry;

    /// <summary>管道名。</summary>
    public string PipeName => _server.PipeName;

    /// <summary>已处理的会话数。</summary>
    public int SessionCount => _server.SessionCount;

    /// <summary>运行期日志。</summary>
    public IReadOnlyList<string> RecentLog => _server.RecentLog;

    private async Task<PipeSessionResult> HandleSessionAsync(PipeSession session, CancellationToken ct)
    {
        string? header;
        try
        {
            header = await session.Reader.ReadLineAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("读取请求行失败：" + ex.Message);
            return PipeSessionResult.Continue;
        }

        if (header is null)
            return PipeSessionResult.Continue;

        _log?.Invoke($"客户端请求：{header}");

        RpcResponse response;
        try
        {
            var request = RpcRequest.ParseHeader(header);

            // 请求体：下一行就是内容，没有内容时是空行或直接断开。
            // 读不到就当空内容 —— 不能因为客户端没写内容行就卡住整个会话。
            string? content = null;
            try
            {
                content = await session.Reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                content = null;
            }

            request = new RpcRequest(request.Type, request.Target, content);
            response = _registry.Dispatch(request, session.ClientProcessId);
        }
        catch (RpcProtocolException ex)
        {
            response = RpcResponse.Error(ex.Reason);
        }
        catch (Exception ex)
        {
            response = RpcResponse.Error(ex.Message);
        }

        try
        {
            response.WriteTo(session.Writer);
            await session.Writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("写响应失败：" + ex.Message);
        }

        return PipeSessionResult.Continue;
    }

    /// <summary>停止服务并释放资源。</summary>
    public async ValueTask DisposeAsync() => await _server.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// 等待服务端真正进入「已停止」状态。
    ///
    /// 为什么需要这个：<see cref="PipeServer"/> 的循环是在**后台线程**里跑的，
    /// <c>DisposeAsync</c> 只是发出取消信号然后等循环体结束 ——
    /// 但循环体此刻可能正卡在 <c>WaitForConnectionAsync</c> 或
    /// 某个 await 上。.NET 的 <c>NamedPipeServerStream</c> 在 macOS/Linux 上
    /// 映射成 Unix domain socket，**取消 WaitForConnection 不一定立刻生效**。
    ///
    /// 不等的话，测试用例退出时管道还没释放，下一个用例用同名管道就会
    /// 撞上「管道已被占用」—— 表现是**随机失败**（这次就撞到过一次：
    /// 整轮 1258 个用例里偶发 1 个失败，重跑就好了）。
    /// </summary>
    public async Task WaitForStoppedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (_server.IsStopped) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
