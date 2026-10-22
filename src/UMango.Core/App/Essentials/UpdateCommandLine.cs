using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 自更新的「助手进程」命令行协议。
///
/// 对应 PCL-CE 的 <c>PCL.Core/App/Essentials/UpdateService.cs</c>（Apache-2.0）的
/// 命令行部分（原版的 <c>LifecycleService</c> IoC 框架不适配 UMango，只取其协议）。
///
/// **为什么要助手进程**：Windows 上正在运行的 exe 无法被自己覆盖
/// （文件被进程锁定，<c>File.Delete</c> 直接 <c>UnauthorizedAccessException</c>）。
/// 所以流程是：主进程启动一个新自己 → 新进程等旧进程退出 → 替换文件 → 重启。
///
/// 命令行格式（<c>UMango.exe</c> 的同一条命令，靠第一个参数分流）：
/// <list type="bullet">
///   <item><c>update &lt;oldPid&gt; &lt;target&gt; &lt;source&gt; &lt;restart&gt;</c> —— 助手模式：等旧进程退出后替换文件并按需重启</item>
///   <item><c>update_finished &lt;source&gt;</c> —— 重启后的收尾：删掉下载来的安装包</item>
///   <item><c>update_failed &lt;reason&gt;</c> —— 重启后的收尾：报告失败原因</item>
/// </list>
/// </summary>
public static class UpdateCommandLine
{
    /// <summary>助手模式子命令名。</summary>
    public const string ApplyCommand = "update";

    /// <summary>更新成功后的收尾子命令名。</summary>
    public const string FinishedCommand = "update_finished";

    /// <summary>更新失败后的收尾子命令名。</summary>
    public const string FailedCommand = "update_failed";

    /// <summary>解析结果：需要执行「等待并替换」。</summary>
    public sealed record ApplyRequest(int OldProcessId, string TargetPath, string SourcePath, bool RestartAfterwards);

    /// <summary>解析结果：需要报告「更新成功，清理临时文件」。</summary>
    public sealed record FinishedRequest(string SourcePath);

    /// <summary>解析结果：需要报告「更新失败」。</summary>
    public sealed record FailedRequest(string Reason);

    /// <summary>
    /// 判断参数是否属于更新协议（调用方据此决定是否进入助手分支而不启动主界面）。
    /// </summary>
    // 严格小写（Ordinal 而非 OrdinalIgnoreCase）：大小写不敏感会连用户自己的
    // "--Update" 之类参数一起劫持进更新分支，那是比漏判更糟的失败模式。
    public static bool IsUpdateCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        (string.Equals(args[0], ApplyCommand, StringComparison.Ordinal) ||
         string.Equals(args[0], FinishedCommand, StringComparison.Ordinal) ||
         string.Equals(args[0], FailedCommand, StringComparison.Ordinal));

    /// <summary>
    /// 解析更新协议参数。
    /// </summary>
    /// <returns>解析出的请求；参数不合法返回 null</returns>
    public static object? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;

        // update <oldPid> <target> <source> <restart>
        if (string.Equals(args[0], ApplyCommand, StringComparison.Ordinal))
        {
            if (args.Count != 5) return null;
            if (!int.TryParse(args[1], out var pid)) return null;
            if (!bool.TryParse(args[4], out var restart)) return null;
            return new ApplyRequest(pid, args[2], args[3], restart);
        }

        // update_finished <source>
        if (string.Equals(args[0], FinishedCommand, StringComparison.Ordinal))
        {
            if (args.Count != 2) return null;
            return new FinishedRequest(args[1]);
        }

        // update_failed <reason>
        if (string.Equals(args[0], FailedCommand, StringComparison.Ordinal))
        {
            if (args.Count != 2) return null;
            return new FailedRequest(args[1]);
        }

        return null;
    }

    /// <summary>
    /// 构造「启动助手进程」的参数（与 <see cref="Parse"/> 互为逆运算）。
    /// </summary>
    public static string[] BuildApplyArguments(int oldProcessId, string targetPath, string sourcePath, bool restart) =>
        new[]
        {
            ApplyCommand,
            oldProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            targetPath,
            sourcePath,
            restart ? "true" : "false"
        };

    /// <summary>
    /// 执行助手模式：等旧进程退出 → 替换文件 → 按需重启 → 返回给调用方的收尾参数。
    /// </summary>
    /// <returns>
    /// 成功时返回重启用的参数（<c>update_finished &lt;source&gt;</c>）；
    /// 失败时返回 <c>update_failed &lt;reason&gt;</c>。
    /// </returns>
    public static string[] RunApply(ApplyRequest request, string? executablePath = null)
    {
        // ① 等旧进程退出（已退出则 GetProcessById 抛 ArgumentException，忽略即可）
        try
        {
            using var old = Process.GetProcessById(request.OldProcessId);
            if (!old.HasExited) old.WaitForExit();
        }
        catch (ArgumentException)
        {
            /* 旧进程已不存在，直接继续 */
        }
        catch (Exception)
        {
            /* 拿不到进程句柄（权限/跨平台差异）也别卡住更新，直接尝试替换 */
        }

        // ② 替换文件
        var ex = UpdateHelper.Replace(request.SourcePath, request.TargetPath);
        if (ex is not null)
            return new[] { FailedCommand, ex.Message };

        // ③ 按需重启，并带上「成功后删安装包」的收尾参数
        if (!request.RestartAfterwards)
            return Array.Empty<string>();

        var startArgs = new[] { FinishedCommand, request.SourcePath };
        StartSelf(executablePath, startArgs);
        return startArgs;
    }

    /// <summary>
    /// 删除更新成功后遗留的安装包（<c>update_finished</c> 分支）。
    /// </summary>
    /// <returns>删除失败时返回异常，否则 null</returns>
    public static Exception? RunFinished(FinishedRequest request)
    {
        try
        {
            if (File.Exists(request.SourcePath)) File.Delete(request.SourcePath);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void StartSelf(string? executablePath, IEnumerable<string> args)
    {
        var path = executablePath;
        if (string.IsNullOrEmpty(path))
        {
            path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;   // 取不到自己就不重启（极端情况）
        }

        try
        {
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = true };
            foreach (var a in args) startInfo.ArgumentList.Add(a);
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            /* 重启失败不抛：调用方已替换完文件，用户手动打开也能用 */
        }
    }
}
