namespace UMango.Core.Execution;

/// <summary>命令行调用结果。</summary>
public sealed class ProcessOutput
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool TimedOut { get; init; }
    public long DurationMs { get; init; }

    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>失败时抛出带上下文的异常（含 stderr 片段，便于界面展示兜底建议）。</summary>
    public void EnsureSuccess(string context)
    {
        if (TimedOut) throw new ProcessException($"{context} 超时", "timeout", StdErr, -1);
        if (ExitCode != 0)
            throw new ProcessException($"{context} 失败（退出码 {ExitCode}）", "command_failed", StdErr, ExitCode);
    }
}

/// <summary>进程调用异常。带机器可读码，供界面做兜底分支。</summary>
public sealed class ProcessException(string message, string code, string stderr, int exitCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public string StdErr { get; } = stderr;
    public int ExitCode { get; } = exitCode;
}
