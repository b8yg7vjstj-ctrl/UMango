// SPDX-License-Identifier: Apache-2.0
//
// CLI 侧的 RPC 客户端：让 `umango rpc …` 能调用**已经在运行**的那个实例。
//
// 参照 PCL-CE 的 `PCL.Core/App/Cli/CliCommandService.cs`（Apache-2.0）里
// 对 RpcService 的调用方式。与原版的差异：原版把「找运行中实例」这件事
// 藏在 PCL 自己的 IoC 里，UMango 改为**在管道名上带进程号**，
// 由第二个实例自己去读锁文件拿名字（与单实例激活通道共用同一份约定）。

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Ipc;
using UMango.Core.Ipc.Rpc;

namespace UMango.Core.App;

/// <summary>
/// 「调已经在运行的 UMango」的命令行入口。
///
/// 用法（管道名由锁文件里的实例提供）：
/// <code>
/// umango rpc REQ ping
/// umango rpc GET version
/// umango rpc REQ install --arg npm
/// </code>
///
/// 三种动词（与 PCL 原版一致）：
/// <list type="bullet">
///   <item><c>REQ &lt;name&gt; [内容]</c> —— 调用函数</item>
///   <item><c>GET &lt;name&gt;</c> —— 读属性</item>
///   <item><c>SET &lt;name&gt; &lt;值&gt;</c> —— 写属性</item>
/// </list>
///
/// **安全**：服务端默认拒绝一切调用（见 <see cref="RpcRegistry"/> 的授权策略），
/// 即使连上了也只会得到一条拒绝消息。这里不做任何绕过。
/// </summary>
public static class RpcCli
{
    /// <summary>运行中实例的管道名发现。</summary>
    public static class InstanceDiscovery
    {
        /// <summary>
        /// RPC 管道名文件路径。
        ///
        /// ⚠ **不是单实例的 <c>instance.lock</c>** —— 那个文件存的是
        /// 「激活通道」的管道名（<c>umango-activate-&lt;pid&gt;</c>），
        /// 协议不一样（激活通道只传一行文本，没有请求/响应结构）。
        /// 拿它发 RPC 会得到一句莫名其妙的话。
        /// 所以 RPC 单独用一个 <c>rpc.pipe</c> 文件。
        /// </summary>
        public static string PipeNameFilePath
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(root, "UMango", "rpc.pipe");
            }
        }

        /// <summary>
        /// 读出运行中实例的管道名。
        /// </summary>
        /// <param name="filePath">管道名文件路径；为 null 时用默认位置。</param>
        /// <returns>管道名；没有运行中的实例时返回 null。</returns>
        public static string? TryReadPipeName(string? filePath = null)
        {
            try
            {
                var path = filePath ?? PipeNameFilePath;
                var name = File.ReadAllText(path).Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception)
            {
                // 读不到 = 没有运行中的实例
                return null;
            }
        }
    }

    /// <summary>
    /// 执行一条 RPC 命令。
    /// </summary>
    /// <param name="args">参数（不含可执行文件名）。</param>
    /// <param name="pipeName">管道名；为 null 时自动发现。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="discoveryFilePath">管道名文件路径；为 null 时用默认位置。</param>
    public static async Task<CliCommandResult> ExecuteAsync(
        string[] args, string? pipeName = null, CancellationToken ct = default, string? discoveryFilePath = null)
    {
        if (args.Length == 0)
            return CliCommandResult.Fail(2, "用法：umango rpc REQ <名称> [内容] | GET <名称> | SET <名称> <值>");

        var verb = args[0].Trim().ToUpperInvariant();
        var target = args.Length > 1 ? args[1].Trim() : string.Empty;

        if (target.Length == 0)
            return CliCommandResult.Fail(2, "缺少目标名称。");

        var name = pipeName ?? InstanceDiscovery.TryReadPipeName(discoveryFilePath);
        if (name is null)
            return CliCommandResult.Fail(3, "没有正在运行的 UMango 实例。");

        RpcRequestType type;
        switch (verb)
        {
            case "REQ":
            case "CALL":
                type = RpcRequestType.Invoke;
                break;
            case "GET":
                type = RpcRequestType.Get;
                break;
            case "SET":
                type = RpcRequestType.Set;
                break;
            default:
                return CliCommandResult.Fail(2, $"无法识别的动词：{args[0]}");
        }

        // 内容：REQ/SET 的第三段（其余参数按空格拼回去）
        var content = args.Length > 2 ? string.Join(' ', args[2..]) : null;

        // REQ 的参数是**跟在函数名后面**的（协议：`REQ func arg`），
        // 而不是放在请求体里 —— 所以要把余下的参数拼到 target 后面。
        if (type == RpcRequestType.Invoke && content is not null)
        {
            target = target + " " + content;
            content = null;
        }

        RpcClientResult result;
        try
        {
            result = await RpcClient.SendAsync(
                new RpcRequest(type, target, content), name, content, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CliCommandResult.Fail(4, $"调用失败：{ex.Message}");
        }

        if (!result.IsSuccess)
        {
            var reason = string.IsNullOrEmpty(result.Content) ? "服务端未说明原因。" : result.Content!;
            return CliCommandResult.Fail(1, reason);
        }

        return result.Type == RpcResponseType.Empty || string.IsNullOrEmpty(result.Content)
            ? CliCommandResult.Ok()
            : CliCommandResult.Ok(result.Content!);
    }
}
