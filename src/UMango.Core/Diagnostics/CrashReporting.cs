// SPDX-License-Identifier: Apache-2.0
//
// 崩溃报告与启动校验 —— 对应 PCL-CE `PCL.Core/App/Essentials/TelemetryService.cs`
// 与 `StartupValidation.cs`（均 Apache-2.0）。
//
// 本文件是**纯 .NET 平台中立层**：不引用 WPF、不引用 Sentry SDK。
//
// 与原版的实质差异（重要）：
//   ① 原版把崩溃/环境数据**直接上报到 Sentry 云端**（DSN 写在
//      `EnvironmentInterop.GetSecret("SENTRY_DSN")`）。UMango **默认不向任何外部
//      服务发送数据** —— 报告只落在本机磁盘；只有用户显式配置了上报地址才会外发。
//   ② 原版的 `StartupValidation.EnsureWpfFont()` 修的是 Windows 上
//      `%windir%` 缺失导致的 WPF 字体渲染崩溃（#3555），是 WPF 专属；
//      UMango 把「启动校验」做成**可注册的检查项列表**（平台中立），
//      WPF 字体检查由 App 层作为一项注册进去。
//   ③ 原版的设备识别码 `Identify.LauncherId` 走 WMI 取主板/CPU 序列号 —
//      隐私敏感且 Windows 专属；UMango 不做机器指纹，只用随机安装标识。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UMango.Core.Diagnostics;

/// <summary>崩溃严重级别。</summary>
public enum CrashSeverity
{
    /// <summary>调试信息。</summary>
    Debug,

    /// <summary>一般信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,

    /// <summary>致命错误（进程即将终止）。</summary>
    Fatal,
}

/// <summary>一份崩溃报告。</summary>
public sealed class CrashReport
{
    /// <summary>报告唯一标识（时间戳 + 随机后缀）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>发生时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>严重级别。</summary>
    public CrashSeverity Severity { get; set; } = CrashSeverity.Error;

    /// <summary>异常类型全名。</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>异常消息。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>异常堆栈。</summary>
    public string? StackTrace { get; set; }

    /// <summary>内部异常链（逐层展开）。</summary>
    public List<string> InnerExceptions { get; set; } = [];

    /// <summary>附带的环境信息快照（键值对，值均为可公开信息）。</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>是否已经成功上报（仅在配置了上报地址时才有意义）。</summary>
    public bool Uploaded { get; set; }
}

/// <summary>崩溃报告的「不收集」判定器。</summary>
/// <remarks>
/// 对应 PCL 原版 `TelemetryService` 里的 `AddExceptionFilterForType&lt;T&gt;()` 系列：
/// 原版把超时、HTTP 失败、取消、目录/文件不存在、权限不足等**可预期异常**
/// 一律丢弃，不上报。UMango 沿用这份清单，且**默认开启**（原版是可选配置）。
/// </remarks>
public static class CrashReportFilter
{
    private static readonly HashSet<Type> ExcludedTypes =
    [
        typeof(TimeoutException),
        typeof(System.Net.Http.HttpRequestException),
        typeof(System.Net.WebException),
        typeof(OperationCanceledException),
        typeof(TaskCanceledException),
        typeof(DirectoryNotFoundException),
        typeof(UnauthorizedAccessException),
        typeof(FileNotFoundException),
    ];

    /// <summary>可预期、不值得记录的异常类型。</summary>
    public static IReadOnlyCollection<Type> Excluded => ExcludedTypes;

    /// <summary>把类型加入排除清单。</summary>
    public static void Exclude(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ExcludedTypes.Add(type);
    }

    /// <summary>从排除清单移除。</summary>
    public static bool Restore(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ExcludedTypes.Remove(type);
    }

    /// <summary>该异常是否应当被丢弃（不生成报告）。</summary>
    public static bool ShouldDrop(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ExcludedTypes.Contains(current.GetType())) return true;
        }

        return false;
    }
}

/// <summary>把异常转成崩溃报告。</summary>
public static class CrashReportFactory
{
    /// <summary>从异常创建报告。</summary>
    public static CrashReport Create(Exception exception, CrashSeverity severity = CrashSeverity.Error,
        string? plainMessage = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var report = new CrashReport
        {
            Id = BuildId(),
            Timestamp = DateTimeOffset.UtcNow,
            Severity = severity,
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = plainMessage is { Length: > 0 } ? plainMessage : exception.Message,
            StackTrace = exception.StackTrace,
        };

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            report.InnerExceptions.Add($"{inner.GetType().FullName}: {inner.Message}");

        if (environment is not null)
            foreach (var pair in environment) report.Environment[pair.Key] = pair.Value;

        return report;
    }

    private static string BuildId()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{stamp}-{suffix}";
    }
}

/// <summary>崩溃报告的本地存储：写 JSON 文件 + 保留最近 N 份。</summary>
public sealed class CrashReportStore
{
    private readonly string _directory;
    private readonly int _keepCount;

    /// <summary>构造。</summary>
    /// <param name="directory">报告目录；不存在会自动创建。</param>
    /// <param name="keepCount">最多保留多少份；小于 1 视为 1。</param>
    public CrashReportStore(string directory, int keepCount = 20)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("报告目录不能为空", nameof(directory));
        _directory = directory;
        _keepCount = keepCount < 1 ? 1 : keepCount;
    }

    /// <summary>报告目录。</summary>
    public string Directory_ => _directory;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>写入一份报告，返回落盘路径；失败返回 null（不抛异常）。</summary>
    public string? Save(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"crash-{report.Id}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, SerializerOptions), Encoding.UTF8);
            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>列出已存的报告，按时间倒序。</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory)) return [];
            return System.IO.Directory.GetFiles(_directory, "crash-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>删除超出保留份数的旧报告。</summary>
    public int Prune()
    {
        var files = List();
        if (files.Count <= _keepCount) return 0;
        var removed = 0;
        foreach (var file in files.Skip(_keepCount))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉就留着，不影响主流程
            }
        }

        return removed;
    }
}

/// <summary>启动校验中的一项检查的结果。</summary>
public sealed class StartupCheckResult
{
    /// <summary>检查项名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否通过。</summary>
    public bool Passed { get; init; }

    /// <summary>失败原因（通过时为空）。</summary>
    public string? Message { get; init; }

    /// <summary>该项是否属于「致命」——致命项失败会阻断启动。</summary>
    public bool Fatal { get; init; }
}

/// <summary>启动校验器：按顺序跑一组检查项，收集结果。</summary>
/// <remarks>
/// 每一项互相隔离：某一项抛异常只让该项失败，不影响其余项
/// （与 PCL `SetupApplier.ApplySafely` 的逐条隔离思路一致）。
/// </remarks>
public sealed class StartupValidator
{
    private readonly List<(string Name, Func<StartupCheckResult> Run, bool Fatal)> _checks = [];

    /// <summary>登记一项检查。</summary>
    public StartupValidator Add(string name, Func<StartupCheckResult> run, bool fatal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        _checks.Add((name, run, fatal));
        return this;
    }

    /// <summary>已登记的检查项数量。</summary>
    public int Count => _checks.Count;

    /// <summary>执行全部检查。</summary>
    public IReadOnlyList<StartupCheckResult> Run()
    {
        var results = new List<StartupCheckResult>(_checks.Count);
        foreach (var (name, run, fatal) in _checks)
        {
            StartupCheckResult result;
            try
            {
                result = run() ?? new StartupCheckResult { Name = name, Passed = false, Message = "检查项未返回结果" };
            }
            catch (Exception ex)
            {
                result = new StartupCheckResult { Name = name, Passed = false, Message = $"{ex.GetType().Name}: {ex.Message}" };
            }

            results.Add(new StartupCheckResult
            {
                Name = result.Name is { Length: > 0 } ? result.Name : name,
                Passed = result.Passed,
                Message = result.Message,
                Fatal = fatal,
            });
        }

        return results;
    }

    /// <summary>是否存在致命项失败。</summary>
    public static bool HasFatalFailure(IReadOnlyList<StartupCheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Any(r => r.Fatal && !r.Passed);
    }
}
