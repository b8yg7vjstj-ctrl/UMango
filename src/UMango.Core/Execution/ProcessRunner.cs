using System.Diagnostics;
using System.Text;

// 注意：本命名空间刻意叫 Execution 而不是 Process —— 后者会与
// System.Diagnostics.Process 撞名，导致 `new Process(...)` 被解析成命名空间而编译失败。
namespace UMango.Core.Execution;

/// <summary>
/// 进程运行器。**终端集成桥的基础**：输出逐行回调，
/// 界面既能拿完整结果，也能实时把 CLI 输出回传到进度页（需求：
/// 调试模式下在下载进度页原生回传终端输出，而非硬嵌黑底终端）。
/// </summary>
public static class ProcessRunner
{
    /// <summary>输出行回调：参数为 (是否 stderr, 行内容)</summary>
    public delegate void OutputLineHandler(bool isStdErr, string line);

    /// <summary>运行一个命令行并等待结束。</summary>
    /// <param name="spec">调用描述</param>
    /// <param name="onLine">可选的逐行回调（用于实时回传终端输出）</param>
    /// <param name="cancelToken">取消令牌</param>
    public static async Task<ProcessOutput> RunAsync(
        ProcessSpec spec,
        OutputLineHandler? onLine = null,
        CancellationToken cancelToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.Program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(spec.WorkingDirectory)) psi.WorkingDirectory = spec.WorkingDirectory;
        // 降低 CLI 的交互与本地化差异，便于稳定解析
        psi.Environment["NO_COLOR"] = "1";
        foreach (var (key, value) in spec.Env) psi.Environment[key] = value;

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine?.Invoke(false, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onLine?.Invoke(true, e.Data);
        };

        // 记录调用（Debug 级）：排查"到底跑没跑、跑的什么"时这是第一手证据。
        // 刻意**不记录输出内容** —— 输出由终端桥负责，且可能是几万行。
        Logging.Log.Debug("CLI", $"执行 {spec.CommandLine}");

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            // 命令不存在 / 不可执行 → 归一化成 cli_missing，界面据此提示"安装 CLI"
            Logging.Log.Error("CLI", ex, $"无法启动 {spec.Program}");
            throw new ProcessException(
                $"无法启动 {spec.Program}：{ex.Message}", "cli_missing", ex.Message, -1);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(spec.TimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancelToken);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }

        // 等待输出流排空，避免丢掉尾部内容
        try { proc.WaitForExit(2000); } catch { /* ignore */ }

        var result = new ProcessOutput
        {
            ExitCode = proc.HasExited ? proc.ExitCode : -1,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            TimedOut = timedOut,
            DurationMs = sw.ElapsedMilliseconds,
        };

        // 结果摘要。失败的记录里带 stderr 片段 —— 真机上用户发回来的日志就靠这一段定位。
        if (result.TimedOut)
        {
            Logging.Log.Warn("CLI", $"{spec.CommandLine} 超时（{result.DurationMs} ms）");
        }
        else if (!result.Success)
        {
            Logging.Log.Warn("CLI",
                $"{spec.CommandLine} 失败（退出码 {result.ExitCode}，{result.DurationMs} ms）：{FirstLines(result.StdErr, 3)}");
        }
        else
        {
            Logging.Log.Debug("CLI", $"{spec.CommandLine} 完成（{result.DurationMs} ms）");
        }

        return result;
    }

    /// <summary>取文本的前若干非空行（日志里只放摘要，不放整份输出）。</summary>
    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(无 stderr)";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .Take(count)
                        .ToList();
        var joined = string.Join(" | ", lines);
        return joined.Length > 300 ? joined[..300] + "…" : joined;
    }

    /// <summary>在 PATH 中查找可执行文件（包源可用性探测用）。</summary>
    public static string? Which(string program)
    {
        if (program.Contains('/') || program.Contains('\\'))
            return File.Exists(program) ? program : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';')
            : [""];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, program + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
