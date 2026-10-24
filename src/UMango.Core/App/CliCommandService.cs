using UMango.Core.Cli;
using UMango.Core.Install;
using UMango.Core.Logging;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;

namespace UMango.Core.App;

/// <summary>
/// 命令行执行结果。
/// </summary>
/// <param name="ExitCode">进程退出码（0 = 成功）。</param>
/// <param name="Lines">输出行（已按顺序产生）。</param>
public sealed record CliCommandResult(int ExitCode, IReadOnlyList<string> Lines)
{
    /// <summary>构造成功结果。</summary>
    public static CliCommandResult Ok(params string[] lines) => new(0, lines);

    /// <summary>构造失败结果。</summary>
    public static CliCommandResult Fail(int exitCode, params string[] lines) => new(exitCode, lines);
}

/// <summary>
/// UMango 命令行命令的执行器。
///
/// 位于 Core（平台中立）的原因：
///   · 命令语义（安装 / 卸载 / 搜索 / 列举）本来就属于业务层；
///   · 有了它就能在**无界面**的环境里测命令行为 —— WPF 窗口在 macOS 上
///     连程序集都加载不了，不能拿它当命令行测试的被测对象。
///
/// 界面层（App）只负责把 <see cref="CliCommandResult"/> 的行打印到控制台，
/// 并在有子命令时**不**打开主窗口。
/// </summary>
public sealed class CliCommandService
{
    private readonly PackageManagerRegistry _registry;
    private readonly InstallExecutor? _executor;

    /// <summary>
    /// 构造命令服务。
    /// </summary>
    /// <param name="registry">包管理器注册表。</param>
    /// <param name="executor">安装执行器；为 null 时 install/uninstall 不可用。</param>
    public CliCommandService(PackageManagerRegistry registry, InstallExecutor? executor = null)
    {
        _registry = registry;
        _executor = executor;
    }

    /// <summary>
    /// 执行一条命令行。
    /// </summary>
    /// <param name="args">原始参数数组（不含可执行文件名）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<CliCommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (!UmangoCommandLine.TryParse(args, out var line) || line is null)
            return CliCommandResult.Ok(HelpText());

        var command = line.SubcommandText ?? line.CommandText;

        // 没有子命令 → 显示帮助
        if (line.Subcommand is null && !IsKnownCommand(command))
            return CliCommandResult.Ok(HelpText());

        return command switch
        {
            "install" => await InstallAsync(line, ct).ConfigureAwait(false),
            "uninstall" => await UninstallAsync(line, ct).ConfigureAwait(false),
            "upgrade" => await UpgradeAsync(line, ct).ConfigureAwait(false),
            "search" => await SearchAsync(line, ct).ConfigureAwait(false),
            "list" => await ListAsync(line, ct).ConfigureAwait(false),
            "source" => await SourceAsync(ct).ConfigureAwait(false),
            "help" => CliCommandResult.Ok(HelpText()),
            _ => CliCommandResult.Ok(HelpText())
        };
    }

    private static bool IsKnownCommand(string text) =>
        UmangoCommandLine.Subcommands.Any(s => string.Equals(s.CommandText, text, StringComparison.Ordinal));

    /// <summary>
    /// 解析 <c>--source</c> 参数（包源种类）。
    ///
    /// 未指定时：按当前平台挑**第一个可用**的包源 ——
    /// 命令行用户通常只想说「装它」，不该被迫先指定包管理器。
    /// </summary>
    private PackageSourceKind? ResolveSource(CommandLine line) => ResolveSource(line, out _);

    /// <summary>
    /// 解析包源。
    /// </summary>
    /// <param name="line">命令行模型。</param>
    /// <param name="error">解析失败的原因（可区分「显式指定了但无法识别」与「本机没有可用包源」）。</param>
    private PackageSourceKind? ResolveSource(CommandLine line, out string? error)
    {
        error = null;
        var (hasSource, sourceOk) = line.TryGetArgumentValue<string>("source", out var raw);
        if (hasSource && sourceOk && !string.IsNullOrWhiteSpace(raw))
        {
            var id = raw!.Trim().ToLowerInvariant();
            foreach (var kind in Enum.GetValues<PackageSourceKind>())
            {
                if (kind.Id() == id) return kind;
            }

            error = $"无法识别的包源：{id}";
            return null;
        }

        var usable = _registry.UsableOnHost.ToList();
        if (usable.Count == 0)
        {
            error = "本机没有可用的包源。";
            return null;
        }

        return usable[0].Kind;
    }

    private async Task<CliCommandResult> InstallAsync(CommandLine line, CancellationToken ct)
    {
        var name = UmangoCommandLine.FirstPositional(line);
        if (string.IsNullOrWhiteSpace(name))
            return CliCommandResult.Fail(2, "用法：umango install <包名> [--source npm] [--version 1.0.0]");

        var source = ResolveSource(line, out var sourceError);
        if (source is null)
            return CliCommandResult.Fail(2, sourceError ?? "无法确定包源。");

        if (_executor is null)
            return CliCommandResult.Fail(3, "当前环境未提供安装执行器。");

        var (hasVersion, versionOk) = line.TryGetArgumentValue<string>("version", out var version);
        var (hasLocal, localOk) = line.TryGetArgumentValue<bool>("local", out var local);
        var global = !(hasLocal && localOk && local);

        var request = new InstallRequest(source.Value, name,
            hasVersion && versionOk && !string.IsNullOrWhiteSpace(version) ? version : null, global);
        var (task, result) = await _executor.InstallAsync(request, ct: ct).ConfigureAwait(false);

        var lines = new List<string> { $"任务：{task.Title}" };
        lines.AddRange(task.Children.Select(c => $"  - {c.Title}：{c.State} {c.StateMessage}".TrimEnd()));
        lines.Add(result.Success ? $"安装成功（{result.DurationMs} ms）" : $"安装失败：{result.Message ?? result.ErrorCode}");
        return new CliCommandResult(result.Success ? 0 : 1, lines);
    }

    private async Task<CliCommandResult> UninstallAsync(CommandLine line, CancellationToken ct)
    {
        var name = UmangoCommandLine.FirstPositional(line);
        if (string.IsNullOrWhiteSpace(name))
            return CliCommandResult.Fail(2, "用法：umango uninstall <包名> [--source npm]");

        var source = ResolveSource(line, out var sourceError);
        if (source is null) return CliCommandResult.Fail(2, sourceError ?? "无法确定包源。");

        var pm = _registry.Find(source.Value);
        if (pm is null) return CliCommandResult.Fail(2, $"没有找到包源：{source.Value.DisplayName()}");

        // 卸载目前没有统一的适配器能力，这里如实说明而不是假装成功。
        var installed = await pm.ListInstalledAsync(ct).ConfigureAwait(false);
        var hit = installed.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return CliCommandResult.Fail(4, $"{pm.DisplayName} 里没有找到已安装的 {name}。");

        return CliCommandResult.Fail(3,
            $"卸载 {name} 尚未在命令行实现（界面里可以卸载）。已安装版本：{hit.Version ?? "未知"}。");
    }

    private Task<CliCommandResult> UpgradeAsync(CommandLine line, CancellationToken ct)
    {
        var name = UmangoCommandLine.FirstPositional(line);
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(CliCommandResult.Fail(2, "用法：umango upgrade <包名> [--source npm]"));

        var source = ResolveSource(line);
        var pm = source is null ? null : _registry.Find(source.Value);
        return Task.FromResult(CliCommandResult.Fail(3,
            $"升级 {name} 尚未在命令行实现" + (pm is null ? "。" : $"（{pm.DisplayName}）。")));
    }

    private async Task<CliCommandResult> SearchAsync(CommandLine line, CancellationToken ct)
    {
        var query = UmangoCommandLine.FirstPositional(line);
        if (string.IsNullOrWhiteSpace(query))
            return CliCommandResult.Fail(2, "用法：umango search <关键词> [--source npm]");

        var source = ResolveSource(line, out var sourceError);
        if (source is null) return CliCommandResult.Fail(2, sourceError ?? "无法确定包源。");

        var pm = _registry.Find(source.Value);
        if (pm is null) return CliCommandResult.Fail(2, $"没有找到包源：{source.Value.DisplayName()}");

        var results = await pm.SearchAsync(query, 20, ct).ConfigureAwait(false);
        var lines = new List<string> { $"{pm.DisplayName} 搜索“{query}”：{results.Count} 个结果" };
        lines.AddRange(results.Select(r => $"  {r.Name}  {r.LatestVersion}  {r.Summary}"));
        return CliCommandResult.Ok(lines.ToArray());
    }

    private async Task<CliCommandResult> ListAsync(CommandLine line, CancellationToken ct)
    {
        var source = ResolveSource(line, out var sourceError);
        if (source is null) return CliCommandResult.Fail(2, sourceError ?? "无法确定包源。");

        var pm = _registry.Find(source.Value);
        if (pm is null) return CliCommandResult.Fail(2, $"没有找到包源：{source.Value.DisplayName()}");

        var installed = await pm.ListInstalledAsync(ct).ConfigureAwait(false);
        var lines = new List<string> { $"{pm.DisplayName} 已安装 {installed.Count} 个包" };
        lines.AddRange(installed.Select(p => $"  {p.Name}  {p.Version ?? "未知"}"));
        return CliCommandResult.Ok(lines.ToArray());
    }

    private async Task<CliCommandResult> SourceAsync(CancellationToken ct)
    {
        var lines = new List<string> { "可用包源：" };
        foreach (var pm in _registry.All)
        {
            var availability = await pm.ProbeAsync(ct).ConfigureAwait(false);
            var mark = availability.IsUsable ? "√" : "×";
            var dir = string.IsNullOrWhiteSpace(pm.InstallDirectory) ? "（未知）" : pm.InstallDirectory;
            lines.Add($"  {mark} {pm.DisplayName}  {dir}  {availability.State}");
        }

        return CliCommandResult.Ok(lines.ToArray());
    }

    /// <summary>帮助文本。</summary>
    public static string[] HelpText() =>
    [
        "UMango —— 跨平台包管理器图形界面",
        "",
        "用法：umango <命令> [参数]",
        "",
        "命令：",
        "  install <包名> [--source npm] [--version 1.0.0] [--local]   安装包",
        "  uninstall <包名> [--source npm]                              卸载包",
        "  upgrade <包名> [--source npm]                                 升级包",
        "  search <关键词> [--source npm]                               搜索包",
        "  list [--source npm]                                          列出已安装",
        "  source                                                       列出包源与可用性",
        "  help                                                         显示本帮助",
        "",
        "通用参数：",
        "  --source <种类>   包源（npm / pnpm / git / scoop / winget / homebrew）",
        "  --version <版本>  指定版本",
        "",
        "不带任何命令时启动图形界面。"
    ];
}
