// SPDX-License-Identifier: Apache-2.0
//
// 崩溃处理器 —— App 侧适配层，把 UMango.Core.Diagnostics 的纯 .NET 能力接到 WPF 生命周期上。
//
// 设计来源：PCL-CE `PCL.Core/App/Essentials/TelemetryService.cs`（Apache-2.0）。
//
// 与原版的实质差异：
//   ① 原版把崩溃数据上报到 Sentry 云端。UMango **默认只落本机磁盘**，
//      不访问任何外部服务；上报地址留空即完全离线。
//   ② 原版用 WMI 取主板/CPU 序列号生成机器识别码。UMango **不做机器指纹**
//      （隐私），环境快照只含操作系统/运行时/应用版本等公开信息。
//   ③ 原版把「可预期异常」清单做成 Sentry 过滤器。UMango 把它做成
//      `CrashReportFilter`（Core 层，可单测），默认丢弃。

using System;
using System.Collections.Generic;
using System.IO;
using UMango.Core.Diagnostics;
using UMango.Core.Logging;

namespace UMango.App.Services;

/// <summary>全局崩溃处理器。</summary>
public static class CrashReporter
{
    private static readonly object Gate = new();
    private static CrashReportStore? _store;

    /// <summary>报告目录（应用日志目录下的 crashes 子目录）。</summary>
    public static string ReportDirectory => Path.Combine(AppServices.LogDirectory, "crashes");

    /// <summary>初始化本地报告存储（幂等）。</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            _store ??= new CrashReportStore(ReportDirectory);
        }
    }

    /// <summary>记录一份崩溃报告；返回落盘路径（被过滤或写盘失败时返回 null）。</summary>
    public static string? Report(Exception exception, CrashSeverity severity = CrashSeverity.Error,
        string? plainMessage = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 可预期异常（超时、取消、文件不存在……）不生成报告 —— 与原版过滤器清单一致。
        if (CrashReportFilter.ShouldDrop(exception)) return null;

        try
        {
            Initialize();
            var report = CrashReportFactory.Create(exception, severity, plainMessage, CollectEnvironment());
            var path = _store!.Save(report);
            if (path is not null) Log.ErrorAt("Crash", exception, member: "Report");
            return path;
        }
        catch (Exception)
        {
            // 崩溃处理器本身绝不能抛 —— 否则会掩盖原始异常。
            return null;
        }
    }

    /// <summary>收集环境快照（只含公开信息，不含机器指纹）。</summary>
    public static Dictionary<string, string> CollectEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["os"] = Environment.OSVersion.ToString(),
            ["osArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtimeVersion"] = Environment.Version.ToString(),
            ["is64Bit"] = Environment.Is64BitProcess.ToString(),
            ["appVersion"] = Core.Platform.AppMetadata.Current.BaseVersion,
        };

        try
        {
            env["installId"] = InstallIdentity.GetOrCreate(
                Path.Combine(AppServices.LogDirectory, "install-id.txt"));
        }
        catch (Exception)
        {
            // 取不到就跳过，不影响其它字段
        }

        return env;
    }

    /// <summary>列出已保存的崩溃报告文件（时间倒序）。</summary>
    public static IReadOnlyList<string> ListReports()
    {
        Initialize();
        return _store!.List();
    }
}
