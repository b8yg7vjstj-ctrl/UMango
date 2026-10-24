// SPDX-License-Identifier: Apache-2.0
//
// RPC 服务的 App 层接入：把「已在运行的 UMango」变成可被命令行/脚本调用的对象。
//
// 参照 PCL-CE 的 `PCL.Core/App/Essentials/RpcService.cs`（Apache-2.0）。
// 与原版的差异见 `src/UMango.Core/Ipc/Rpc/RpcProtocol.cs` 头部。

using System;
using System.IO;
using System.Threading.Tasks;
using UMango.Core.Ipc;
using UMango.Core.Ipc.Rpc;

namespace UMango.App.Services;

/// <summary>
/// RPC 宿主：在应用启动时起一个命名管道 RPC 服务。
///
/// **默认什么都不开放**（见 <see cref="RpcRegistry"/> 的授权策略）——
/// 命名管道没有鉴权，默认放行等于本机任何进程都能调 UMango。
/// 想开放某个能力，必须同时做两件事：
///   ① 显式注册那个函数/属性；
///   ② 显式把授权策略从「全部拒绝」改成能放行它的策略。
/// 少任何一步都调不到 —— 这是刻意的「两道手续」。
/// </summary>
public sealed class RpcHost : IAsyncDisposable
{
    private readonly RpcService _service;
    private readonly Action<string>? _log;

    /// <summary>创建宿主。</summary>
    /// <param name="pipeName">管道名；为 null 时按进程号生成。</param>
    /// <param name="log">日志回调。</param>
    public RpcHost(string? pipeName = null, Action<string>? log = null)
    {
        _log = log;
        var name = pipeName ?? BuildDefaultPipeName();
        PipeName = name;

        var registry = new RpcRegistry();

        // 内置函数：ping 是**唯一**在默认策略下也放行的（它不碰任何状态，
        // 只回一个固定字符串）。但默认策略是全部拒绝，所以严格来说
        // 连 ping 也要显式放行 —— 见 <see cref="InstallDefaultEndpoints"/>。
        _service = new RpcService(name, registry, log);
    }

    /// <summary>管道名。</summary>
    public string PipeName { get; }

    /// <summary>
    /// 记录当前运行实例的 RPC 管道名的文件路径。
    ///
    /// **为什么单独一个文件**，而不是塞进单实例的 <c>instance.lock</c>：
    /// 那个文件存的是**激活通道**的管道名（<c>umango-activate-&lt;pid&gt;</c>），
    /// 两者用途不同、生命周期也不同（激活管道在进程退出时销毁）。
    /// 混在一处的话，第二个实例读到的名字可能是激活管道，
    /// 拿它去发 RPC 请求会得到一个格式不对的响应。
    /// </summary>
    public static string PipeNameFilePath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "UMango", "rpc.pipe");
        }
    }

    /// <summary>把管道名写到发现文件里（供命令行找过来）。</summary>
    public void PublishPipeName()
    {
        var dir = Path.GetDirectoryName(PipeNameFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(PipeNameFilePath, PipeName);
    }

    /// <summary>删除发现文件。</summary>
    public static void RemovePipeNameFile()
    {
        try
        {
            if (File.Exists(PipeNameFilePath)) File.Delete(PipeNameFilePath);
        }
        catch (Exception)
        {
            // 删不掉不影响退出
        }
    }

    /// <summary>底层服务（注册函数/属性用）。</summary>
    public RpcService Service => _service;

    /// <summary>已处理的会话数。</summary>
    public int SessionCount => _service.SessionCount;

    /// <summary>
    /// 生成默认管道名。
    ///
    /// 名字带进程号，所以**同一时刻只有一个实例**能占用这个名字 ——
    /// 这顺带解决了「第二个实例会不会也起一个 RPC 服务」的问题。
    /// 但它**不是安全边界**：进程号是公开的，别的进程照样能连上。
    /// </summary>
    public static string BuildDefaultPipeName() =>
        PipeServer.BuildPipeName("umango-rpc", Environment.ProcessId.ToString());

    /// <summary>
    /// 注册内置的端点，并把授权策略换成「放行已注册的名字」。
    ///
    /// 调用意图：注册表里有的名字才放行（<b>白名单</b>），
    /// 没注册的一律拒绝。
    /// </summary>
    public void InstallDefaultEndpoints()
    {
        var registry = _service.Registry;

        registry.AddFunction("ping", (_, _, _) => RpcResponse.Success("pong"));
        registry.AddProperty(new RpcProperty("version", () => AppVersion));

        // 授权：只放行**已注册**的名字。函数与属性共用一张白名单。
        registry.Authorization = (name, _, _) =>
            registry.FindFunction(name) is not null || registry.FindProperty(name) is not null;
    }

    /// <summary>应用版本（供 RPC 读）。</summary>
    public static string AppVersion =>
        typeof(RpcHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>停止服务并释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync().ConfigureAwait(false);
        await _service.WaitForStoppedAsync().ConfigureAwait(false);
    }
}
