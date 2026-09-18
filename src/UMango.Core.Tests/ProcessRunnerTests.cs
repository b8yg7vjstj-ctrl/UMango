namespace UMango.Core.Tests;

/// <summary>
/// 需求 6/7：**ProcessRunner 的定位与容错**。
///
/// 覆盖两条真实路径：
///   · Which —— PATH 查找（找不到必须返回 null，而不是抛异常或返回空串）；
///   · RunAsync —— 正常路径（stdout 采集 + 逐行回调，终端集成桥的数据来源）、
///     超时路径（TimeoutMs 到点必须返回 TimedOut=true 而不是抛异常）、
///     外部取消路径（要与超时区分开）、失败路径（非零退出码 + EnsureSuccess 归一化）。
/// 依赖 /bin/sh 的用例只在类 Unix 主机上执行（macOS 上 /bin/sh、/bin/echo 必定存在）；
/// 在 Windows 上改测 cmd.exe，保证不会因为换机器而静默通过。
/// </summary>
public sealed class ProcessRunnerTests
{
    private static bool IsUnixLike => !OperatingSystem.IsWindows();

    [Fact]
    public void Which_能找到必定存在的命令_找不到的命令返回null()
    {
        if (IsUnixLike)
        {
            Assert.NotNull(ProcessRunner.Which("/bin/sh"));   // 绝对路径：直接判定存在性
            Assert.NotNull(ProcessRunner.Which("sh"));        // PATH 查找
            Assert.NotNull(ProcessRunner.Which("curl"));      // 系统自带的常用命令
        }
        else
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Assert.NotNull(ProcessRunner.Which(cmd));
            Assert.NotNull(ProcessRunner.Which("cmd"));
        }

        var missing = "umango-definitely-missing-" + Guid.NewGuid().ToString("N");
        Assert.Null(ProcessRunner.Which(missing));                       // PATH 里没有
        Assert.Null(ProcessRunner.Which("/no/such/dir/" + missing));     // 绝对路径不存在
    }

    [Fact]
    public async Task 正常路径_采集stdout并逐行回调()
    {
        if (!IsUnixLike) return;

        var received = new List<string>();
        var output = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = "/bin/sh",
                Args = ["-c", "echo hello-from-sh"],
                TimeoutMs = 10_000,
            },
            (isStdErr, line) => received.Add((isStdErr ? "E:" : "O:") + line),
            CancellationToken.None);

        Assert.True(output.Success);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("hello-from-sh", output.StdOut);
        Assert.DoesNotContain("not-json", output.StdOut);
        // 逐行回调 = 调试模式下把 CLI 输出回传到进度页的数据来源
        Assert.Contains("O:hello-from-sh", received);
        Assert.False(output.TimedOut);
    }

    [Fact]
    public async Task 超时路径_返回TimedOut而不是抛异常()
    {
        if (!IsUnixLike) return;

        var output = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = "/bin/sh",
                Args = ["-c", "sleep 5"],
                TimeoutMs = 300,
            },
            null,
            CancellationToken.None);

        Assert.True(output.TimedOut, "TimeoutMs=300 到点后必须标记 TimedOut");
        Assert.False(output.Success);
        // 确实被超时打断，而不是等 sleep 跑完（否则耗时会在 5 秒以上）
        Assert.True(output.DurationMs < 4_000, $"超时应立即生效，实际耗时 {output.DurationMs} ms");
    }

    [Fact]
    public async Task 外部取消路径_不算超时_两者必须可区分()
    {
        if (!IsUnixLike) return;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var output = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = "/bin/sh",
                Args = ["-c", "sleep 5"],
                TimeoutMs = 60_000,   // 超时远未到点：这里的终止只能来自外部取消
            },
            null,
            cts.Token);

        Assert.False(output.TimedOut);   // 界面要能区分"用户取消"和"超时"
        Assert.False(output.Success);
    }

    [Fact]
    public async Task 失败路径_非零退出码被归一化为command_failed()
    {
        if (!IsUnixLike) return;

        var output = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = "/bin/sh",
                Args = ["-c", "echo boom >&2; exit 3"],
                TimeoutMs = 10_000,
            },
            null,
            CancellationToken.None);

        Assert.False(output.Success);
        Assert.Equal(3, output.ExitCode);
        Assert.Contains("boom", output.StdErr);

        var ex = Assert.Throws<ProcessException>(() => output.EnsureSuccess("测试命令"));
        Assert.Equal("command_failed", ex.Code);
        Assert.Equal(3, ex.ExitCode);
    }

    [Fact]
    public async Task 命令不存在_归一化为cli_missing()
    {
        var ex = await Assert.ThrowsAsync<ProcessException>(() => ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = "/no/such/dir/umango-missing-" + Guid.NewGuid().ToString("N"),
                TimeoutMs = 5_000,
            },
            null,
            CancellationToken.None));

        Assert.Equal("cli_missing", ex.Code);   // 界面据此提示"先安装该 CLI"
    }

    [Fact]
    public void ProcessSpec_CommandLine用于日志展示()
    {
        var spec = ProcessSpec.Of("npm", "view", "express", "--json");
        Assert.Equal("npm view express --json", spec.CommandLine);
        Assert.Equal(120_000, spec.TimeoutMs);   // 默认超时
    }
}
