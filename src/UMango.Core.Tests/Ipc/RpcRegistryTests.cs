using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Ipc.Rpc;
using Xunit;

namespace UMango.Core.Tests.Ipc;

/// <summary>
/// RPC 的协议解析与分发。
///
/// 分发逻辑是纯函数（不碰 IO），所以这些用例直接测行为，不需要起管道。
/// </summary>
public class RpcRegistryTests
{
    private static RpcRegistry AllowAll() => new(RpcAuthorizationPolicies.AllowAll);

    // ---------------- 协议解析 ----------------

    [Fact]
    public void 请求行_解析出_GET()
    {
        var request = RpcRequest.ParseHeader("GET version");
        Assert.Equal(RpcRequestType.Get, request.Type);
        Assert.Equal("version", request.Target);
    }

    [Fact]
    public void 请求行_解析出_SET()
    {
        var request = RpcRequest.ParseHeader("SET theme");
        Assert.Equal(RpcRequestType.Set, request.Type);
        Assert.Equal("theme", request.Target);
    }

    [Fact]
    public void 请求行_解析出_REQ_并保留参数()
    {
        var request = RpcRequest.ParseHeader("REQ install npm express");
        Assert.Equal(RpcRequestType.Invoke, request.Type);
        Assert.Equal("install npm express", request.Target);
    }

    [Fact]
    public void 请求行_动词忽略大小写()
    {
        var request = RpcRequest.ParseHeader("get version");
        Assert.Equal(RpcRequestType.Get, request.Type);
    }

    [Fact]
    public void 请求行为空时抛协议异常()
    {
        Assert.Throws<RpcProtocolException>(() => RpcRequest.ParseHeader(null));
        Assert.Throws<RpcProtocolException>(() => RpcRequest.ParseHeader(""));
    }

    [Fact]
    public void 请求行缺目标时抛协议异常()
    {
        Assert.Throws<RpcProtocolException>(() => RpcRequest.ParseHeader("GET"));
        Assert.Throws<RpcProtocolException>(() => RpcRequest.ParseHeader("GET "));
    }

    [Fact]
    public void 请求行动词不认识时抛协议异常()
    {
        var ex = Assert.Throws<RpcProtocolException>(() => RpcRequest.ParseHeader("DELETE version"));
        Assert.Contains("GET/SET/REQ", ex.Reason);
    }

    // ---------------- 分发：函数 ----------------

    [Fact]
    public void 调用已注册函数会拿到它的响应()
    {
        var registry = AllowAll();
        registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "ping", null));

        Assert.Equal(RpcResponseStatus.Success, response.Status);
        Assert.Equal("pong", response.Content);
    }

    [Fact]
    public void 调用不存在的函数返回错误而不是抛异常()
    {
        var registry = AllowAll();
        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "nope", null));
        Assert.Equal(RpcResponseStatus.Error, response.Status);
        Assert.Contains("nope", response.Content);
    }

    [Fact]
    public void 函数参数与内容会原样传进去()
    {
        string? gotArg = null;
        string? gotContent = null;
        var registry = AllowAll();
        registry.AddFunction("f", (arg, content, _) =>
        {
            gotArg = arg;
            gotContent = content;
            return RpcResponse.EmptySuccess;
        });

        registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "f hello world", "body"));

        Assert.Equal("hello world", gotArg);
        Assert.Equal("body", gotContent);
    }

    [Fact]
    public void 函数名末尾的美元号表示缩进且不参与查表()
    {
        bool gotIndent = false;
        var registry = AllowAll();
        registry.AddFunction("f", (_, _, indent) =>
        {
            gotIndent = indent;
            return RpcResponse.EmptySuccess;
        });

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "f$", null));

        Assert.True(gotIndent);
        Assert.Equal(RpcResponseStatus.Success, response.Status);
    }

    [Fact]
    public void 函数抛异常时返回错误而不是把异常抛出去()
    {
        var registry = AllowAll();
        registry.AddFunction("boom", (_, _, _) => throw new InvalidOperationException("炸了"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "boom", null));

        Assert.Equal(RpcResponseStatus.Error, response.Status);
        Assert.Contains("炸了", response.Content);
    }

    // ---------------- 分发：属性 ----------------

    [Fact]
    public void 读属性拿到值()
    {
        var registry = AllowAll();
        registry.AddProperty(new RpcProperty("version", () => "1.2.3"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Get, "version", null));

        Assert.Equal(RpcResponseStatus.Success, response.Status);
        Assert.Equal("1.2.3", response.Content);
    }

    [Fact]
    public void 读不存在的属性返回错误()
    {
        var registry = AllowAll();
        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Get, "nope", null));
        Assert.Equal(RpcResponseStatus.Error, response.Status);
    }

    [Fact]
    public void 写属性会调用写回调()
    {
        string? written = null;
        var registry = AllowAll();
        registry.AddProperty(new RpcProperty("theme", () => "light", v => written = v));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Set, "theme", "dark"));

        Assert.Equal(RpcResponseStatus.Success, response.Status);
        Assert.Equal("dark", written);
    }

    [Fact]
    public void 写只读属性返回失败()
    {
        var registry = AllowAll();
        registry.AddProperty(new RpcProperty("ro", () => "x"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Set, "ro", "y"));

        Assert.Equal(RpcResponseStatus.Failure, response.Status);
    }

    [Fact]
    public void 写回调抛异常时返回失败()
    {
        var registry = AllowAll();
        registry.AddProperty(new RpcProperty("p", () => "x", _ => throw new InvalidOperationException()));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Set, "p", "y"));

        Assert.Equal(RpcResponseStatus.Failure, response.Status);
    }

    // ---------------- 授权 ----------------

    [Fact]
    public void 默认策略是全部拒绝()
    {
        var registry = new RpcRegistry();
        registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "ping", null));

        Assert.Equal(RpcResponseStatus.Error, response.Status);
        Assert.Contains("拒绝", response.Content);
    }

    [Fact]
    public void 授权策略能挡住读属性()
    {
        var registry = new RpcRegistry((_, _, _) => false);
        registry.AddProperty(new RpcProperty("v", () => "1"));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Get, "v", null));

        Assert.Equal(RpcResponseStatus.Error, response.Status);
    }

    [Fact]
    public void 授权策略能挡住写属性()
    {
        var registry = new RpcRegistry((_, _, _) => false);
        registry.AddProperty(new RpcProperty("v", () => "1", _ => { }));

        var response = registry.Dispatch(new RpcRequest(RpcRequestType.Set, "v", "2"));

        Assert.Equal(RpcResponseStatus.Error, response.Status);
    }

    [Fact]
    public void 授权策略能拿到调用方进程号()
    {
        int? seen = null;
        var registry = new RpcRegistry((_, _, pid) =>
        {
            seen = pid;
            return true;
        });
        registry.AddFunction("f", (_, _, _) => RpcResponse.EmptySuccess);

        registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "f", null), 4242);

        Assert.Equal(4242, seen);
    }

    // ---------------- 注册表管理 ----------------

    [Fact]
    public void 函数名与属性名忽略大小写()
    {
        var registry = AllowAll();
        registry.AddFunction("Ping", (_, _, _) => RpcResponse.Success("pong"));
        registry.AddProperty(new RpcProperty("Version", () => "1"));

        Assert.Equal(RpcResponseStatus.Success,
            registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "ping", null)).Status);
        Assert.Equal(RpcResponseStatus.Success,
            registry.Dispatch(new RpcRequest(RpcRequestType.Get, "version", null)).Status);
    }

    [Fact]
    public void 移除函数后调用会报不存在()
    {
        var registry = AllowAll();
        registry.AddFunction("f", (_, _, _) => RpcResponse.EmptySuccess);
        Assert.True(registry.RemoveFunction("f"));

        Assert.Equal(RpcResponseStatus.Error,
            registry.Dispatch(new RpcRequest(RpcRequestType.Invoke, "f", null)).Status);
    }

    [Fact]
    public void 移除属性后读会报不存在()
    {
        var registry = AllowAll();
        registry.AddProperty(new RpcProperty("p", () => "1"));
        Assert.True(registry.RemoveProperty("p"));

        Assert.Equal(RpcResponseStatus.Error,
            registry.Dispatch(new RpcRequest(RpcRequestType.Get, "p", null)).Status);
    }

    [Fact]
    public void 能列出已注册的函数与属性()
    {
        var registry = AllowAll();
        registry.AddFunction("f", (_, _, _) => RpcResponse.EmptySuccess);
        registry.AddProperty(new RpcProperty("p", () => "1"));

        Assert.Contains("f", registry.FunctionNames);
        Assert.Contains("p", registry.PropertyNames);
    }
}

/// <summary>RPC 响应的序列化格式。</summary>
public class RpcResponseTests
{
    [Fact]
    public void 空成功响应写成一行()
    {
        var text = RpcResponse.EmptySuccess.ToString();
        Assert.Equal("SUCCESS empty", text);
    }

    [Fact]
    public void 带内容的响应写成两行()
    {
        var text = RpcResponse.Success("hi", "greeting").ToString();
        Assert.Equal("SUCCESS text greeting\nhi", text);
    }

    [Fact]
    public void 错误响应是_ERROR_开头()
    {
        Assert.StartsWith("ERROR text", RpcResponse.Error("坏了").ToString());
    }

    [Fact]
    public void 空类型的响应不能带内容()
    {
        Assert.Throws<ArgumentException>(() =>
            new RpcResponse(RpcResponseStatus.Success, RpcResponseType.Empty, "有内容"));
    }

    [Fact]
    public void 写进流的内容与_ToString_一致()
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            RpcResponse.Success("hi", "greeting").WriteTo(writer);
            writer.Flush();
        }

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("SUCCESS text greeting", text);
        Assert.Contains("hi", text);
    }
}
