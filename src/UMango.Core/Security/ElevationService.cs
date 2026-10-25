// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路取自 PCL-CE 的
// PCL.Core/App/Essentials/PromoteService.cs（Apache-2.0 许可）。
//
// 采用时做的改动（详见本仓库既定的「语义重写」做法）：
//   1. PCL 用「启动自己的一份 `promote <pid>` 进程 + 命名管道通信」实现提权，
//      整套机制耦合在 PCL 的 IoC 生命周期与 `Lifecycle.RequestStopLoading()` 上，
//      且明确注明「TODO 提权进程自动启动」（原版默认不自动拉起）。
//      UMango 不引入 PCL 的 IoC，改为**自包含的提升通道**：
//      宿主进程负责「决定要不要提权 + 发起 + 收结果」，
//      提权侧的**可执行操作集合**由宿主注册（与 PCL 的
//      `AddOperationFunction` 同样的语义，只是注册表不放在提权进程里）。
//   2. PCL 的**来源校验**（比对提权进程的可执行文件路径与主进程是否一致）
//      保留 —— 这是防止别的程序连上管道发命令的关键。
//   3. PCL 的管道名是 `PCLCE_PM@<pid>`；UMango 改用已有的
//      `PipeServer.BuildPipeName`（macOS/Linux 上 Unix domain socket 路径 ≤ 104 字符）。
//   4. PCL 的「主进程/提权进程」靠命令行参数 `promote <pid>` 区分；
//      UMango 改用**显式的提升请求**（`ElevationRequest`），并保留
//      「同一程序路径」的来源校验。
//   5. PCL 的错误码是硬编码字符串常量（`ERR_OPERATION_NOT_FOUND` 等）；
//      这里改成枚举 + 稳定的字符串映射，便于跨语言/跨进程断言。
//
// **跨平台立场**：Windows 上通过 `runas` 动词请求 UAC；
// macOS 上用 `osascript ... with administrator privileges`；
// Linux 上用 `pkexec`。三者都**不能**在 Core 层用 Process.Start 直接做
// （macOS 的 osascript 需要拼 shell 字符串，有注入风险），
// 所以 Core 层只提供**判定与协议**，真正发起由平台适配器完成。

using System.Globalization;

namespace UMango.Core.Security;

/// <summary>提权通道的协议常量。</summary>
public static class ElevationProtocol
{
    /// <summary>管道名前缀。</summary>
    public const string PipePrefix = "umango-elev";

    /// <summary>操作未找到。</summary>
    public const string ErrOperationNotFound = "ERR_OPERATION_NOT_FOUND";

    /// <summary>参数非法。</summary>
    public const string ErrIllegalArgument = "ERR_ILLEGAL_ARGUMENT";

    /// <summary>操作内部抛异常。</summary>
    public const string ErrUnhandledException = "ERR_UNHANDLED_EXCEPTION";

    /// <summary>操作返回空。</summary>
    public const string ErrEmpty = "ERR_EMPTY";

    /// <summary>来源校验失败（连上来的不是同一个程序）。</summary>
    public const string ErrSourceRejected = "ERR_SOURCE_REJECTED";

    /// <summary>提权通道没有就绪。</summary>
    public const string ErrChannelUnavailable = "ERR_CHANNEL_UNAVAILABLE";
}

/// <summary>一个提权请求。</summary>
/// <param name="Operation">操作名。</param>
/// <param name="Argument">操作参数（单行文本；内部换行会被压成空格）。</param>
public readonly record struct ElevationRequest(string Operation, string? Argument = null)
{
    /// <summary>序列化成协议行：<c>操作名 参数</c>（参数为空时只有操作名）。</summary>
    public string ToLine()
    {
        var op = Operation ?? string.Empty;
        if (string.IsNullOrEmpty(Argument))
            return op;
        return op + " " + Flatten(Argument);
    }

    /// <summary>把多行文本压成单行（协议是一行一条消息）。</summary>
    public static string Flatten(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}

/// <summary>提权操作的执行结果。</summary>
public sealed class ElevationResult
{
    /// <summary>构造结果。</summary>
    /// <param name="ok">是否成功。</param>
    /// <param name="value">返回值（成功时；失败时为错误码）。</param>
    /// <param name="error">错误码（失败时）。</param>
    public ElevationResult(bool ok, string? value, string? error = null)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    /// <summary>是否成功。</summary>
    public bool Ok { get; }

    /// <summary>返回值。</summary>
    public string? Value { get; }

    /// <summary>错误码。</summary>
    public string? Error { get; }

    /// <summary>成功结果。</summary>
    public static ElevationResult Success(string? value) => new(true, value);

    /// <summary>失败结果。</summary>
    public static ElevationResult Failure(string error) => new(false, null, error);

    /// <inheritdoc />
    public override string ToString() => Ok ? $"OK({Value})" : $"ERR({Error})";
}

/// <summary>
/// 提权通道的**操作注册表**。
///
/// 与 PCL 的 <c>PromoteService._OperationFunctions</c> 同语义：
/// 操作名 → 实现。差别是 PCL 的注册表在**提权进程**里（提权进程启动时
/// 由依赖注入扫描收集），UMango 的注册表在**宿主进程**里 ——
/// 提权通道只是一个「把请求转发给已注册操作」的通道。
/// </summary>
public sealed class ElevationOperationRegistry
{
    private readonly Dictionary<string, Func<string?, string?>> _operations = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>注册一个操作。</summary>
    /// <param name="name">操作名（不能与已注册的重复）。</param>
    /// <param name="operation">操作实现：接收参数、返回结果（可为空）。</param>
    /// <returns>注册成功返回 true；名字为空或已存在返回 false。</returns>
    public bool Register(string name, Func<string?, string?> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_gate)
        {
            if (_operations.ContainsKey(name))
                return false;
            _operations[name] = operation;
            return true;
        }
    }

    /// <summary>注销一个操作。</summary>
    public bool Unregister(string name)
    {
        lock (_gate)
            return _operations.Remove(name);
    }

    /// <summary>是否已注册。</summary>
    public bool IsRegistered(string name)
    {
        lock (_gate)
            return _operations.ContainsKey(name);
    }

    /// <summary>已注册的操作名。</summary>
    public IReadOnlyList<string> RegisteredNames
    {
        get { lock (_gate) return _operations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
    }

    /// <summary>
    /// 执行一个请求。
    /// </summary>
    /// <remarks>
    /// 错误语义与 PCL 一致：操作不存在 → <see cref="ElevationProtocol.ErrOperationNotFound"/>；
    /// 操作抛异常 → <see cref="ElevationProtocol.ErrUnhandledException"/>；
    /// 操作返回 null → <see cref="ElevationProtocol.ErrEmpty"/>。
    /// </remarks>
    public ElevationResult Execute(ElevationRequest request)
    {
        Func<string?, string?>? operation;
        lock (_gate)
        {
            _operations.TryGetValue(request.Operation ?? string.Empty, out operation);
        }

        if (operation is null)
            return ElevationResult.Failure(ElevationProtocol.ErrOperationNotFound);

        try
        {
            var result = operation(request.Argument);
            return result is null
                ? ElevationResult.Failure(ElevationProtocol.ErrEmpty)
                : ElevationResult.Success(result);
        }
        catch
        {
            return ElevationResult.Failure(ElevationProtocol.ErrUnhandledException);
        }
    }
}

/// <summary>各平台发起提权的方式。</summary>
public enum ElevationMethod
{
    /// <summary>不支持提权。</summary>
    Unsupported = 0,

    /// <summary>Windows UAC（<c>runas</c> 动词）。</summary>
    WindowsRunAs,

    /// <summary>macOS <c>osascript with administrator privileges</c>。</summary>
    MacOsScript,

    /// <summary>Linux <c>pkexec</c>。</summary>
    LinuxPkExec,
}

/// <summary>提权能力的判定结果。</summary>
/// <param name="Supported">当前平台是否支持提权。</param>
/// <param name="Method">提权方式。</param>
/// <param name="Reason">不支持时的原因（供界面提示）。</param>
public readonly record struct ElevationAvailability(bool Supported, ElevationMethod Method, string? Reason = null)
{
    /// <summary>不支持提权的判定结果。</summary>
    public static ElevationAvailability NotSupported(string reason) =>
        new(false, ElevationMethod.Unsupported, reason);
}

/// <summary>
/// 提权能力判定。
///
/// 与 PCL 的差异：PCL 完全依赖 Windows 的 <c>runas</c>，在 macOS/Linux 上
/// 什么都不做。UMango 显式建模三个平台的做法，界面可以据此提示用户
/// 「本平台需要手动授权」。
/// </summary>
public static class ElevationCapability
{
    /// <summary>判定当前平台是否可提权。</summary>
    public static ElevationAvailability Detect(UMango.Core.Platform.PlatformKind platform)
    {
        switch (platform)
        {
            case UMango.Core.Platform.PlatformKind.Windows:
                return new ElevationAvailability(true, ElevationMethod.WindowsRunAs);
            case UMango.Core.Platform.PlatformKind.MacOs:
                return new ElevationAvailability(true, ElevationMethod.MacOsScript);
            case UMango.Core.Platform.PlatformKind.Linux:
                return new ElevationAvailability(true, ElevationMethod.LinuxPkExec);
            default:
                return ElevationAvailability.NotSupported("当前平台没有可用的提权方式");
        }
    }

    /// <summary>判定当前平台是否可提权。</summary>
    public static ElevationAvailability Detect() => Detect(UMango.Core.Platform.SystemInfo.Platform);
}

/// <summary>
/// 提权来源校验。
///
/// PCL 的实现：提权进程拿到主进程 id 后，用
/// <c>process.MainModule.FileName</c> 比对主进程的可执行文件路径，
/// 不一致就退出。这是**防止别的程序连上管道发命令**的关键防线 ——
/// 管道名只含 pid，理论上任何本地程序都能猜到。
/// </summary>
public static class ElevationSourceGuard
{
    /// <summary>校验来源进程的可执行文件路径是否与本程序一致。</summary>
    /// <param name="sourceExecutablePath">来源进程的可执行文件路径（可为空 → 拒绝）。</param>
    /// <param name="ownExecutablePath">本程序的可执行文件路径。</param>
    /// <returns>通过返回 true。</returns>
    public static bool IsSameProgram(string? sourceExecutablePath, string? ownExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(sourceExecutablePath) || string.IsNullOrWhiteSpace(ownExecutablePath))
            return false;

        try
        {
            var a = Path.GetFullPath(sourceExecutablePath);
            var b = Path.GetFullPath(ownExecutablePath);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>比较两个可执行文件路径是否指向同一个文件。</summary>
    public static bool IsSameProgram(FileInfo? source, string? ownExecutablePath) =>
        IsSameProgram(source?.FullName, ownExecutablePath);
}

/// <summary>提权命令的构造结果。</summary>
/// <param name="Program">要启动的程序。</param>
/// <param name="Arguments">参数列表。</param>
/// <param name="UseShellExecute">是否用 ShellExecute（Windows 的 runas 动词需要它）。</param>
/// <param name="Verb">Windows Shell 动词（"runas"）。</param>
public readonly record struct ElevationLaunch(string Program, IReadOnlyList<string> Arguments, bool UseShellExecute, string? Verb = null)
{
    /// <summary>拼成可读的命令行（仅用于日志与断言）。</summary>
    public string CommandLine =>
        Arguments.Count == 0
            ? Program
            : Program + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal) ? "\"" + arg + "\"" : arg;
}

/// <summary>
/// 提权命令构造器。
///
/// **为什么在 Core 层而不是直接 Process.Start**：Windows 的 <c>runas</c> 动词
/// 需要 <c>UseShellExecute = true</c>，而 macOS 的 <c>osascript</c> 需要
/// 把命令拼成 <c>do shell script "…" with administrator privileges</c> ——
/// 里面必须正确转义引号，否则用户输入的路径里的引号会**逃逸出** shell 字符串。
/// 拼接逻辑必须可测，所以放 Core。
/// </summary>
public static class ElevationCommandBuilder
{
    /// <summary>构造 Windows 的 <c>runas</c> 启动参数。</summary>
    /// <param name="program">要提权启动的程序。</param>
    /// <param name="arguments">参数。</param>
    public static ElevationLaunch WindowsRunAs(string program, IReadOnlyList<string>? arguments = null) =>
        new(program, arguments ?? [], UseShellExecute: true, Verb: "runas");

    /// <summary>
    /// 构造 macOS 的 <c>osascript</c> 提权命令。
    /// </summary>
    /// <param name="program">要提权启动的程序。</param>
    /// <param name="arguments">参数。</param>
    public static ElevationLaunch MacOsScript(string program, IReadOnlyList<string>? arguments = null)
    {
        var args = arguments ?? [];

        // 逐个参数做 POSIX shell 单引号转义，再把整条命令行塞进
        // AppleScript 的字符串字面量里（字面量本身要转义 \ 与 "）。
        // 注意：**不能再对整条命令行套一层 shell 引号** ——
        // 那会让 shell 把整条命令当成一个单词，参数就全丢了。
        var shellCommand = args.Count == 0
            ? QuoteShell(program)
            : QuoteShell(program) + " " + string.Join(' ', args.Select(QuoteShell));

        var script = "do shell script " + QuoteAppleScript(shellCommand) +
                     " with administrator privileges";

        return new ElevationLaunch("osascript", ["-e", script], UseShellExecute: false);
    }

    /// <summary>构造 Linux 的 <c>pkexec</c> 提权命令。</summary>
    public static ElevationLaunch LinuxPkExec(string program, IReadOnlyList<string>? arguments = null) =>
        new("pkexec", [program, .. (arguments ?? [])], UseShellExecute: false);

    /// <summary>按平台构造提权命令。</summary>
    public static ElevationLaunch Build(UMango.Core.Platform.PlatformKind platform, string program, IReadOnlyList<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        return platform switch
        {
            UMango.Core.Platform.PlatformKind.Windows => WindowsRunAs(program, arguments),
            UMango.Core.Platform.PlatformKind.MacOs => MacOsScript(program, arguments),
            _ => LinuxPkExec(program, arguments),
        };
    }

    /// <summary>按当前平台构造提权命令。</summary>
    public static ElevationLaunch Build(string program, IReadOnlyList<string>? arguments = null) =>
        Build(UMango.Core.Platform.SystemInfo.Platform, program, arguments);

    /// <summary>POSIX shell 的单引号转义（<c>'</c> → <c>'\''</c>）。</summary>
    public static string QuoteShell(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>AppleScript 字符串字面量的转义（<c>\</c> 与 <c>"</c>）。</summary>
    public static string QuoteAppleScript(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                     .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// 判断一个命令是否需要提权。
    ///
    /// Windows 上「需要提权」的判定比较复杂（文件 ACL、清单、注册表），
    /// 这里只做**保守近似**：目标是系统目录下的可执行文件时建议提权。
    /// 这只是一个提示，不代替真正的 UAC 判断。
    /// </summary>
    public static bool LooksPrivileged(string? targetPath, UMango.Core.Platform.PlatformKind platform)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || platform != UMango.Core.Platform.PlatformKind.Windows)
            return false;

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
            return false;

        try
        {
            var full = Path.GetFullPath(targetPath);
            var root = Path.GetFullPath(system);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>提权通道的状态。</summary>
public enum ElevationChannelState
{
    /// <summary>未启动。</summary>
    Idle = 0,

    /// <summary>已就绪，可以接收请求。</summary>
    Ready,

    /// <summary>已停止。</summary>
    Stopped,
}

/// <summary>
/// 提权通道（宿主侧）。
///
/// 职责：
///   1. 维护一条命名管道，接受提权侧的请求；
///   2. 校验来源（必须是同一个程序）；
///   3. 把请求交给 <see cref="ElevationOperationRegistry"/> 执行；
///   4. 把结果按行回写。
///
/// 与 PCL 的差别见文件头。**这里不含任何平台专属的进程启动逻辑** ——
/// 发起提权由 <see cref="ElevationCommandBuilder"/> 产出参数，
/// 真正的 <c>Process.Start</c> 由 App 层执行。
/// </summary>
public sealed class ElevationChannel : IDisposable, IAsyncDisposable
{
    private readonly ElevationOperationRegistry _registry;
    private readonly string _pipeName;
    private readonly string _ownExecutablePath;
    private readonly Func<string?, bool>? _sourceValidator;
    private CancellationTokenSource? _cts;
    private int _state = (int)ElevationChannelState.Idle;

    /// <summary>构造提权通道。</summary>
    /// <param name="registry">操作注册表。</param>
    /// <param name="ownExecutablePath">本程序的可执行文件路径（用于来源校验）。</param>
    /// <param name="sourceValidator">额外的来源校验回调（可为空）。</param>
    public ElevationChannel(
        ElevationOperationRegistry registry,
        string ownExecutablePath,
        Func<string?, bool>? sourceValidator = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ownExecutablePath = ownExecutablePath ?? string.Empty;
        _sourceValidator = sourceValidator;
        _pipeName = Ipc.PipeServer.BuildPipeName(
            ElevationProtocol.PipePrefix,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>管道名。</summary>
    public string PipeName => _pipeName;

    /// <summary>通道状态。</summary>
    public ElevationChannelState State => (ElevationChannelState)Volatile.Read(ref _state);

    /// <summary>已处理的请求数。</summary>
    public int HandledCount { get; private set; }

    /// <summary>
    /// 启动通道。
    ///
    /// 这里用**内存队列 + 协议处理**的方式实现，便于在没有真实提权进程的环境里
    /// 完整测试协议语义。真实提权进程接入时走同一条协议。
    /// </summary>
    public void Start()
    {
        if (State != ElevationChannelState.Idle)
            return;

        _cts = new CancellationTokenSource();
        Volatile.Write(ref _state, (int)ElevationChannelState.Ready);
    }

    /// <summary>停止通道。</summary>
    public void Stop()
    {
        _cts?.Cancel();
        Volatile.Write(ref _state, (int)ElevationChannelState.Stopped);
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 处理一行请求（协议入口）。
    ///
    /// 协议：<c>操作名 [参数]</c>。返回响应行。
    /// </summary>
    public string HandleLine(string? line)
    {
        if (State != ElevationChannelState.Ready)
            return ElevationProtocol.ErrChannelUnavailable;

        if (string.IsNullOrWhiteSpace(line))
            return ElevationProtocol.ErrIllegalArgument;

        var text = ElevationRequest.Flatten(line);
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var operation = space < 0 ? text : text[..space];
        var argument = space < 0 ? null : text[(space + 1)..];

        if (string.IsNullOrWhiteSpace(operation))
            return ElevationProtocol.ErrIllegalArgument;

        var result = _registry.Execute(new ElevationRequest(operation, argument));
        HandledCount++;
        return result.Ok ? result.Value ?? ElevationProtocol.ErrEmpty : result.Error!;
    }

    /// <summary>
    /// 校验来源进程是否被允许。
    /// </summary>
    /// <param name="sourceExecutablePath">来源进程的可执行文件路径。</param>
    public bool IsSourceAllowed(string? sourceExecutablePath)
    {
        if (!ElevationSourceGuard.IsSameProgram(sourceExecutablePath, _ownExecutablePath))
            return false;
        return _sourceValidator?.Invoke(sourceExecutablePath) ?? true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
