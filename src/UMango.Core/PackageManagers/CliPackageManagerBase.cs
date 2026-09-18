using System.Text.Json;
using UMango.Core.Execution;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 命令行型包管理器的公共基类。
///
/// **降低重复代码率的主要抓手**：把六种包管理器都要做的事收口到这里 ——
///   1. 平台门控（不支持的系统不执行任何命令）；
///   2. CLI 定位（PATH 查找）与版本探测；
///   3. CLI 调用（统一超时、统一错误归一化为带机器可读码的异常）；
///   4. **容错 JSON 解析**（CLI 常把警告混进 stdout，前后夹带非 JSON 文本）；
///   5. 未实现能力的统一"不支持"响应。
/// 各适配器只需实现自己那几条命令与解析。
/// </summary>
public abstract class CliPackageManagerBase : IPackageManager
{
    public abstract PackageSourceKind Kind { get; }
    public abstract SourceCapabilities Capabilities { get; }
    public abstract string InstallHint { get; }

    /// <summary>版本探测参数，默认 --version</summary>
    protected virtual string[] VersionArgs => ["--version"];

    /// <summary>
    /// 平台默认安装目录（探测 CLII 之前的兜底值）。
    /// 真实值由 <see cref="InstallDirArgs"/> 在探测时解析并缓存。
    /// </summary>
    protected abstract string DefaultInstallDirectory { get; }

    /// <summary>
    /// 用于查询真实安装目录的命令参数，例如 npm 的 ["root","-g"]、brew 的 ["--prefix"]。
    /// 为 null 表示该包管理器没有可靠的查询方式，直接用平台默认值。
    /// </summary>
    protected virtual string[]? InstallDirArgs => null;

    private string? _resolvedInstallDir;

    /// <summary>
    /// **界面上作为子标题显示的安装目录**。
    /// 已探测过则返回 CLI 报出的真实路径，否则返回平台默认值。
    /// </summary>
    public string InstallDirectory => _resolvedInstallDir ?? DefaultInstallDirectory;

    /// <summary>
    /// 用缓存补种已解析的安装目录。
    /// 只在还没有真实值时补 —— 已经探测出来的值不该被旧缓存覆盖。
    /// </summary>
    public void SeedResolvedInstallDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        _resolvedInstallDir ??= directory;
    }

    /// <summary>默认超时。包管理器首次调用可能要更新索引，给足时间。</summary>
    protected virtual int DefaultTimeoutMs => 120_000;

    public virtual string DisplayName => Kind.DisplayName();

    public string CliName => Kind switch
    {
        PackageSourceKind.Npm => "npm",
        PackageSourceKind.Pnpm => "pnpm",
        PackageSourceKind.Git => "git",
        PackageSourceKind.Scoop => "scoop",
        PackageSourceKind.Winget => "winget",
        _ => "brew"
    };

    public IReadOnlyList<PlatformKind> SupportedPlatforms => Kind.SupportedPlatforms();

    // ---------- 可用性探测（共性逻辑，子类一般不需要覆盖） ----------

    public virtual async Task<SourceAvailability> ProbeAsync(CancellationToken ct = default)
    {
        if (!Kind.Supports(SystemInfo.Platform))
        {
            return new SourceAvailability
            {
                Kind = Kind,
                State = AvailabilityState.UnsupportedPlatform,
                Detail = $"{DisplayName} 仅在 {string.Join(" / ", SupportedPlatforms.Select(p => p.DisplayName()))} 上可用",
                VerificationNote = VerificationNote
            };
        }

        var path = ProcessRunner.Which(CliName);
        if (path is null)
        {
            return new SourceAvailability
            {
                Kind = Kind,
                State = AvailabilityState.NotInstalled,
                CliFound = false,
                Detail = $"在 PATH 中找不到 {CliName}",
                VerificationNote = VerificationNote
            };
        }

        try
        {
            var output = await ProcessRunner.RunAsync(
                new ProcessSpec { Program = path, Args = VersionArgs.ToList(), TimeoutMs = 15_000 }, null, ct)
                .ConfigureAwait(false);

            // 顺带解析真实安装目录（失败不影响可用性判定）
            await ResolveInstallDirectoryAsync(path, ct).ConfigureAwait(false);
            var firstLine = output.StdOut.Split('\n').Select(l => l.Trim())
                                   .FirstOrDefault(l => l.Length > 0);
            return new SourceAvailability
            {
                Kind = Kind,
                State = output.Success ? AvailabilityState.Available : AvailabilityState.Degraded,
                CliFound = true,
                CliPath = path,
                CliVersion = firstLine,
                Detail = output.Success ? null : Truncate(output.StdErr, 200),
                VerificationNote = VerificationNote,
                ResolvedInstallDirectory = _resolvedInstallDir
            };
        }
        catch (Exception ex)
        {
            return new SourceAvailability
            {
                Kind = Kind,
                State = AvailabilityState.Unreachable,
                CliFound = true,
                CliPath = path,
                Detail = ex.Message,
                VerificationNote = VerificationNote
            };
        }
    }

    /// <summary>
    /// 该适配器的解析逻辑是否已在真机验证过。
    /// 未在真机验证的适配器必须如实标注（界面会显示"未验证"提示）。
    /// </summary>
    protected virtual string? VerificationNote => null;

    /// <summary>
    /// 把上面那个 protected 的标注暴露成接口成员，供注册表在**平台门控**时也能取到
    /// （门控路径不调用 ProbeAsync，否则就等于为了拿文案而去执行 CLI）。
    /// 用显式接口实现是为了不与子类覆盖的 protected 属性重名 ——
    /// 六个适配器仍然只覆盖 protected 的那个，无需改动。
    /// </summary>
    string? IPackageManager.VerificationNote => VerificationNote;

    // ---------- CLI 调用（共性） ----------

    /// <summary>
    /// 运行该包管理器的 CLI，返回原始输出。
    ///
    /// **这里是平台门控的唯一入口**：不支持当前平台的包管理器（macOS 上的 winget/scoop）
    /// 会直接抛出 unsupported_platform，**根本不会去起进程**。
    ///
    /// 为什么把门控放在这里而不是各个方法里：
    /// 曾经出现过「ProbeAsync 做了门控，但 ListInstalledAsync 忘了」的真实缺陷 ——
    /// 结果是 macOS 上真的去启动 winget 进程并抛"No such file or directory"。
    /// 放在唯一入口上，任何适配器的任何 CLI 调用都不可能绕过。
    /// </summary>
    protected Task<ProcessOutput> RunCliAsync(
        IEnumerable<string> args,
        ProcessRunner.OutputLineHandler? onLine = null,
        int? timeoutMs = null,
        CancellationToken ct = default)
    {
        if (!Kind.Supports(SystemInfo.Platform))
        {
            throw new ProcessException(
                $"{DisplayName} 不支持 {SystemInfo.Platform.DisplayName()}，已阻止执行 {CliName}",
                "unsupported_platform", "", -1);
        }

        var spec = new ProcessSpec
        {
            Program = CliName,
            Args = args.ToList(),
            TimeoutMs = timeoutMs ?? DefaultTimeoutMs
        };
        return ProcessRunner.RunAsync(spec, onLine, ct);
    }

    /// <summary>
    /// 容错解析 JSON。
    ///
    /// 真机经验：npm / pnpm / brew 都会把警告、进度提示混进 stdout，
    /// 直接 JsonSerializer.Deserialize 会失败。这里兜住三种情况：
    ///   1. 前后夹带杂项 → 截取第一个 { 或 [ 到最后一个 } 或 ]；
    ///   2. 输出为空 → 返回 default（空输出在 CLI 里往往表示"无内容"而非错误）；
    ///   3. 彻底解析失败 → 抛带原始片段的可读异常（便于用户回报真实格式）。
    /// </summary>
    protected static T? ParseJson<T>(string raw, string context)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(trimmed);
        }
        catch (JsonException)
        {
            var start = trimmed.IndexOfAny(['{', '[']);
            var end = trimmed.LastIndexOfAny(['}', ']']);
            if (start >= 0 && end > start)
            {
                var slice = trimmed[start..(end + 1)];
                try
                {
                    return JsonSerializer.Deserialize<T>(slice);
                }
                catch (JsonException ex)
                {
                    throw new ProcessException(
                        $"解析 {context} 的输出失败：{ex.Message}；片段：{Truncate(slice, 200)}",
                        "parse_error", slice, -1);
                }
            }
            throw new ProcessException(
                $"解析 {context} 的输出失败：找不到 JSON 起始字符；片段：{Truncate(trimmed, 200)}",
                "parse_error", trimmed, -1);
        }
    }

    /// <summary>判断输出是否表示"包不存在"，归一化为 package_not_found。</summary>
    protected static bool LooksLikeNotFound(string stderr, string stdout)
    {
        var s = (stderr + " " + stdout).ToLowerInvariant();
        return s.Contains("e404")
            || s.Contains("404 not found")
            || s.Contains("not found")
            || s.Contains("is not in this registry")
            || s.Contains("no package found")
            || s.Contains("not installed");
    }

    /// <summary>查询并缓存真实安装目录。失败时静默保留平台默认值。</summary>
    private async Task ResolveInstallDirectoryAsync(string cliPath, CancellationToken ct)
    {
        if (InstallDirArgs is null) return;
        try
        {
            var out2 = await ProcessRunner.RunAsync(
                new ProcessSpec { Program = cliPath, Args = InstallDirArgs.ToList(), TimeoutMs = 15_000 }, null, ct)
                .ConfigureAwait(false);
            if (!out2.Success) return;
            var line = out2.StdOut.Split('\n').Select(l => l.Trim())
                             .FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrEmpty(line)) _resolvedInstallDir = line;
        }
        catch (Exception)
        {
            // 解析失败不是错误：子标题退回平台默认值即可
        }
    }

    protected static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ---------- 能力方法：默认"不支持"，子类按需覆盖 ----------

    public virtual Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<PackageInfo>>(Unsupported("搜索"));

    public virtual Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<PackageVersion>>(Unsupported("版本列表"));

    public virtual Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
        => Task.FromException<IReadOnlyList<InstalledPackage>>(Unsupported("已安装列表"));

    public virtual Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DependencyRef>>([]);

    public virtual Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InstallStep>>([
            new InstallStep { Title = $"安装 {name}", Method = DownloadMethod.Unknown }
        ]);

    protected ProcessException Unsupported(string feature) =>
        new($"{DisplayName} 不支持{feature}", "unsupported_feature", "", -1);
}
