// SPDX-License-Identifier: Apache-2.0
//
// 提权启动器（App 层）。
//
// Core 的 `ElevationService` 只负责**判定与协议**（Core 是纯 .NET，
// 不能直接 Process.Start，也不能拼 shell 字符串）。
// 真正发起提权进程的动作放在这里，并抽出一个可替换的
// `IProcessStarter` 接口，方便在测试里断言「发起了什么命令」。

using System.Diagnostics;
using UMango.Core.Logging;
using UMango.Core.Security;

namespace UMango.App.Services;

/// <summary>启动一个外部进程的抽象（便于测试）。</summary>
public interface IProcessStarter
{
    /// <summary>启动进程；返回进程 id（失败时返回 null）。</summary>
    /// <param name="launch">要启动的命令。</param>
    int? Start(ElevationLaunch launch);
}

/// <summary>基于 <see cref="Process"/> 的真实启动器。</summary>
public sealed class SystemProcessStarter : IProcessStarter
{
    /// <inheritdoc />
    public int? Start(ElevationLaunch launch)
    {
        var info = new ProcessStartInfo
        {
            FileName = launch.Program,
            UseShellExecute = launch.UseShellExecute,
        };

        if (!string.IsNullOrEmpty(launch.Verb))
            info.Verb = launch.Verb;

        foreach (var argument in launch.Arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            return process?.Id;
        }
        catch (Exception ex)
        {
            // 提权失败最常见的原因是「用户取消了 UAC 对话框」——
            // 那会抛 Win32Exception。这里不该炸掉整个调用方。
            Log.ErrorAt("Elevation", ex, member: "Start");
            return null;
        }
    }
}

/// <summary>提权启动的结果。</summary>
public sealed class ElevationLaunchResult
{
    /// <summary>构造结果。</summary>
    /// <param name="succeeded">是否成功启动。</param>
    /// <param name="processId">进程 id（成功时）。</param>
    /// <param name="reason">失败原因（失败时）。</param>
    public ElevationLaunchResult(bool succeeded, int? processId, string? reason = null)
    {
        Succeeded = succeeded;
        ProcessId = processId;
        Reason = reason;
    }

    /// <summary>是否成功。</summary>
    public bool Succeeded { get; }

    /// <summary>进程 id。</summary>
    public int? ProcessId { get; }

    /// <summary>失败原因。</summary>
    public string? Reason { get; }
}

/// <summary>
/// 提权启动器。
///
/// PCL-CE 的 <c>PromoteService</c> 是「宿主进程 + 提权进程 + 命名管道」三件套：
/// 宿主启动一份自己的副本（带 <c>promote &lt;pid&gt;</c> 参数），
/// 提权进程连回宿主的管道执行请求。
/// UMango 保留「同一程序、来源校验、白名单操作」这三条核心语义，
/// 但**发起提权**这一步可以走两种方式：
///   ① <see cref="Launch"/>：以提权身份启动**任意程序**（runas / osascript / pkexec）；
///   ② 走 <see cref="UMango.Core.Security.ElevationChannel"/> 的操作注册表 ——
///      宿主注册操作，提权侧只拿到「能执行哪些操作」的白名单。
/// </summary>
public sealed class ElevationLauncher
{
    private readonly IProcessStarter _starter;
    private readonly Func<ElevationAvailability>? _availabilityOverride;

    /// <summary>构造提权启动器。</summary>
    /// <param name="starter">进程启动器（可换成假实现测试）。</param>
    /// <param name="availabilityOverride">
    /// 覆盖提权能力判定的委托（可为空）。
    ///
    /// **为什么需要它**：能力判定依赖当前运行的平台，而「当前平台不支持提权」
    /// 这条分支在开发机（macOS）上永远走不到 —— 那条分支就没有测试守着，
    /// 等于没有实现。这里开出这个口子，让测试能强制走进「不支持」路径。
    /// </param>
    public ElevationLauncher(
        IProcessStarter? starter = null,
        Func<ElevationAvailability>? availabilityOverride = null)
    {
        _starter = starter ?? new SystemProcessStarter();
        _availabilityOverride = availabilityOverride;
    }

    /// <summary>当前平台的提权能力。</summary>
    public ElevationAvailability Availability =>
        _availabilityOverride?.Invoke() ?? ElevationCapability.Detect();

    /// <summary>
    /// 以提权身份启动一个程序。
    /// </summary>
    /// <param name="program">要启动的程序路径。</param>
    /// <param name="arguments">参数。</param>
    public ElevationLaunchResult Launch(string program, IReadOnlyList<string>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(program))
            return new ElevationLaunchResult(false, null, "未指定程序");

        var availability = Availability;
        if (!availability.Supported)
            return new ElevationLaunchResult(false, null, availability.Reason ?? "当前平台不支持提权");

        var launch = ElevationCommandBuilder.Build(program, arguments);
        Log.Info("Elevation", $"请求提权启动：{launch.CommandLine}");

        var pid = _starter.Start(launch);
        if (pid is null)
            return new ElevationLaunchResult(false, null, "提权进程未能启动（用户可能取消了授权）");

        Log.Info("Elevation", $"提权进程已启动，pid={pid}");
        return new ElevationLaunchResult(true, pid);
    }

    /// <summary>
    /// 以提权身份启动当前程序自身（PCL 的 <c>promote &lt;pid&gt;</c> 模式的通用版）。
    /// </summary>
    /// <param name="arguments">传给提权副本的参数。</param>
    public ElevationLaunchResult LaunchSelf(IReadOnlyList<string>? arguments = null)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self))
            return new ElevationLaunchResult(false, null, "无法确定当前程序路径");

        return Launch(self, arguments);
    }
}
