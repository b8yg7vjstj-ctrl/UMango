using System.Text.Json;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// Homebrew 适配器（macOS / Linux）。
///
/// **真机验证过的事实**（macOS + Homebrew 6.0.22 + /opt/homebrew）：
///   · `brew --version` 首行形如 `Homebrew 6.0.22`；
///   · `brew --prefix` 输出安装前缀（Apple Silicon 为 /opt/homebrew，Intel 为 /usr/local）；
///   · **`brew search` 没有 JSON 输出**（传 --json 只会打印 usage），所以搜索必须两步走：
///       ① `brew search --formula &lt;query&gt;` 取纯文本名字列表；
///       ② 再用**一次**批量的 `brew info --json=v2 &lt;n1&gt; &lt;n2&gt; …` 拿回全部详情
///          （实测支持多包同查，顶层结构 `{ "formulae": [ … ] }`）；
///   · `brew info --json=v2` 只要**其中一个名字不存在就整体失败**（退出码 1、stdout 为空），
///     所以批量失败时必须降级（见 <see cref="FetchFormulaDetailsAsync"/>）；
///   · 官方安装量在 https://formulae.brew.sh/api/formula/&lt;name&gt;.json 的
///     analytics.install.30d 里，是一个对象（例如 {"wget":9306,"wget --HEAD":28}）。
///
/// 本适配器的解析逻辑已在上述真机上逐条核对，故 VerificationNote 返回 null（= 已验证，无需提示）。
/// </summary>
public sealed class HomebrewPackageManager : CliPackageManagerBase
{
    /// <summary>
    /// 官方安装量查询的并发上限（避免一次起几十个 curl 进程）。
    /// 注意：详情查询是**单次批量调用**，不吃这个上限。
    /// </summary>
    private const int MaxEnrichConcurrency = 6;

    /// <summary>调用方传入 limit &lt;= 0 时的默认结果条数</summary>
    private const int DefaultSearchLimit = 20;

    private const string AnalyticsSource = "formulae.brew.sh 近30天安装量";

    public override PackageSourceKind Kind => PackageSourceKind.Homebrew;

    /// <summary>
    /// 能力声明。**OfficialDownloadCounts = true 是 Homebrew 相对 pnpm/scoop/winget 的优势**：
    /// formulae.brew.sh 提供官方匿名安装统计，不像其它包源只能拿 GitHub Stars 兜底。
    /// </summary>
    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: true,
        OfficialDownloadCounts: true, Install: true, Uninstall: true, Upgrade: true);

    public override string InstallHint =>
        "安装 Homebrew：https://brew.sh —— 一条命令即可（macOS 与 Linux 通用）。"
        + "国内网络可给安装脚本的 GitHub 地址加 http://gh-proxy.org/ 前缀加速。";

    /// <summary>
    /// 平台默认安装前缀。仅在探测出 `brew --prefix` 之前用作兜底：
    /// macOS 官方规则是 Apple Silicon → /opt/homebrew、Intel → /usr/local；
    /// Linux 官方规则是 /home/linuxbrew/.linuxbrew。
    /// </summary>
    protected override string DefaultInstallDirectory => SystemInfo.Platform switch
    {
        PlatformKind.MacOs => SystemInfo.Architecture == "arm64" ? "/opt/homebrew" : "/usr/local",
        _ => "/home/linuxbrew/.linuxbrew"
    };

    /// <summary>
    /// `brew --prefix` 是最权威的安装前缀（界面子标题直接显示它）。
    /// 注意：这个前缀既是 Homebrew 自身所在处，也是它的安装根目录（Cellar 在其下）。
    /// </summary>
    protected override string[]? InstallDirArgs => ["--prefix"];

    /// <summary>
    /// brew 首次调用可能触发自动更新索引（HOMEBREW_NO_AUTO_UPDATE 未在本工程的进程运行器里设置，
    /// 因为 ProcessSpec 目前不支持附加环境变量），所以比基类默认值再宽一点。
    /// </summary>
    protected override int DefaultTimeoutMs => 180_000;

    /// <summary>
    /// 解析逻辑已在真机（macOS + Homebrew 6.0.22）逐条核对，因此如实返回 null：
    /// 界面对 null 不显示"未验证"提示。基类默认就是 null，这里显式写出以表明是**有意为之**。
    /// </summary>
    protected override string? VerificationNote => null;

    // ---------- 搜索 ----------

    /// <summary>
    /// 搜索 formula。**两步法**（brew search 无 JSON 输出）：
    ///   ① `brew search --formula &lt;query&gt;` —— 纯文本，一行可能含多个空格分隔的名字，
    ///      并可能夹带 "==&gt; Formulae" 段标题与 "───" 之类的分隔线，需要过滤；
    ///   ② `brew info --json=v2 &lt;name1&gt; … &lt;nameN&gt;` —— 一次批量拿详情，
    ///      避免 N 次进程启动。
    /// 最后对每条结果用 curl 取官方安装量（失败则留空，绝不编造）。
    /// </summary>
    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var take = limit <= 0 ? DefaultSearchLimit : limit;

        // ① 纯文本搜索。brew search --formula 只返回 formula，不会混入 cask（已实测）。
        var search = await RunCliAsync(["search", "--formula", query], null, null, ct).ConfigureAwait(false);
        // 用户取消时进程被 kill，退出码会是 137 之类的信号值：先如实抛取消，不要误报成命令失败。
        ct.ThrowIfCancellationRequested();
        if (!search.Success)
        {
            // 无匹配时 brew 的 stderr 是：Error: No formulae or casks found for "xxx"（退出码 1）
            if (IsFormulaNotFound(search.StdErr, search.StdOut))
                throw new ProcessException(
                    $"找不到与 {query} 匹配的 formula", "package_not_found", search.StdErr, search.ExitCode);
            search.EnsureSuccess($"brew search --formula {query}");
        }

        // brew 按字母序输出，与"相关度"无关；这里把精确命中/前缀命中提到前面，再截断到 limit。
        var names = RankByRelevance(ParseSearchNames(search.StdOut), query).Take(take).ToList();
        if (names.Count == 0) return [];

        // ② 一次批量取详情。批量查询只要有一个名字失效就整体失败（实测），
        //    此时降级为"只有名字"的结果，而不是让整次搜索失败。
        Dictionary<string, FormulaSnapshot> details;
        try
        {
            details = await FetchFormulaDetailsAsync(names, ct).ConfigureAwait(false);
        }
        catch (ProcessException)
        {
            details = new Dictionary<string, FormulaSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        // ③ 官方安装量（并行取，最多 MaxEnrichConcurrency 路；取不到就不填）
        var counts = await FetchInstallCountsAsync(names, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var list = new List<PackageInfo>(names.Count);
        foreach (var name in names)
        {
            if (!details.TryGetValue(name, out var f))
            {
                // 详情拿不到（离线 / tap 异常）时只给出名字，保证列表不空
                list.Add(new PackageInfo { Source = Kind, Name = name });
                continue;
            }

            var summary = f.Desc;
            if (f.Deprecated)
            {
                var reason = string.IsNullOrWhiteSpace(f.DeprecationReason) ? "" : $"：{f.DeprecationReason}";
                summary = string.IsNullOrEmpty(summary) ? $"已废弃{reason}" : $"{summary}（已废弃{reason}）";
            }
            else if (f.Disabled)
            {
                var reason = string.IsNullOrWhiteSpace(f.DisableReason) ? "" : $"：{f.DisableReason}";
                summary = string.IsNullOrEmpty(summary) ? $"已停用{reason}" : $"{summary}（已停用{reason}）";
            }

            // 注意用 long? 承接：字典里没有该键时不能落成 0（0 是"有数据但没人装"的假信息）
            long? count = counts.TryGetValue(name, out var c) ? c : null;
            list.Add(new PackageInfo
            {
                Source = Kind,
                Name = name,
                Summary = summary,
                LatestVersion = f.Stable,
                License = f.License,
                Homepage = f.Homepage,
                // 取到官方安装量才填；DownloadCountIsFallback 保持 false（这是**官方**数据，不是 Stars 兜底）
                DownloadCount = count,
                DownloadCountSource = count is null ? null : AnalyticsSource,
                DownloadCountIsFallback = false,
            });
        }
        return list;
    }

    /// <summary>
    /// 解析 `brew search --formula` 的纯文本输出。
    /// 实测输出形态（Homebrew 6.0.22，无段标题）：
    /// <code>
    /// wget
    /// wget2
    /// wgetpaste
    /// </code>
    /// 但旧版/带 tap 的结果会出现段标题与分隔线，且一行可能有多个名字：
    /// <code>
    /// ==&gt; Formulae
    /// foo bar
    /// ────────────
    /// </code>
    /// 因此逐行过滤段标题、分隔线与 Error 行，再把行内按空格拆成名字。
    /// </summary>
    private static List<string> ParseSearchNames(string stdout)
    {
        var names = new List<string>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("==>", StringComparison.Ordinal)) continue;          // 段标题
            if (line.Contains('─') || line.Contains('—') || line.Contains('–')) continue; // 分隔线
            if (line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith('#')) continue;                                       // 注释行

            foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsFormulaName(token)) names.Add(token);
            }
        }
        return names;
    }

    /// <summary>粗略校验 token 是否是合法 formula 名（字母数字与 @ + . _ -，且至少含一个字母）。</summary>
    private static bool IsFormulaName(string token)
    {
        if (token.Length == 0 || token[0] == '-') return false;   // 过滤 --flag 与选项
        var hasLetter = false;
        foreach (var c in token)
        {
            if (char.IsLetter(c)) { hasLetter = true; continue; }
            if (char.IsDigit(c)) continue;
            if (c is '@' or '+' or '.' or '_' or '-') continue;
            return false;
        }
        return hasLetter;
    }

    /// <summary>
    /// 相关度重排：精确命中 → 前缀命中 → 其余，组内**短名优先**（名字越短越贴近查询），
    /// 最后用名字做最终 tie-break，保证结果可复现。
    /// 例：查 "python" 时 python@3.14 会排在 python-argcomplete 之前，
    /// 否则 brew 的字母序会把真正想要的 formula 埋掉（实测排序："-" 在 "@" 之前）。
    /// </summary>
    private static IEnumerable<string> RankByRelevance(IEnumerable<string> names, string query)
    {
        var q = query.Trim();
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                        : n.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);
    }

    // ---------- 版本列表 ----------

    /// <summary>
    /// 版本列表。**如实反映 Homebrew 的版本模型**：
    ///   · `versions.stable` —— 仓库里唯一的当前版本（Homebrew 不保留历史版本号）；
    ///   · `versioned_formulae` —— 同族其它**主版本**以独立 formula 形式存在
    ///     （例如 python@3.11 的 versioned_formulae = python@3.14/@3.13/@3.12/@3.10/@3.9），
    ///     它们各自有独立的 stable 版本，所以这里再批量查一次它们的版本号；
    ///   · `versions.head` 非空时补一个 "HEAD" 条目并标记为预发布。
    /// </summary>
    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ProcessException("未指定 formula 名", "invalid_argument", "", -1);

        // 单包查询：失败要区分"包不存在"与"命令失败"，所以走 throwOnFailure
        var details = await FetchFormulaDetailsAsync([name], ct, throwOnFailure: true).ConfigureAwait(false);
        if (!details.TryGetValue(name, out var f))
            throw new ProcessException($"找不到 formula {name}", "package_not_found", "", 0);

        var list = new List<PackageVersion>();

        // ---- 当前 stable ----
        var installed = f.InstalledVersions.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var note = "Homebrew 只保留当前 stable 版本，不保留历史版本号：旧主版本以**独立 formula** 形式存在"
                 + "（例如 python@3.11 本身就是独立 formula），需要哪个主版本就安装哪个。";
        if (installed is not null)
        {
            note += f.Outdated
                ? $" 本机已安装 {installed}，落后于 stable，可执行 brew upgrade 升级。"
                : $" 本机已安装：{installed}。";
        }

        if (!string.IsNullOrWhiteSpace(f.Stable))
        {
            list.Add(new PackageVersion
            {
                Version = f.Stable!,
                Deprecated = f.Deprecated,
                Note = note,
            });
        }

        // ---- 同族的其它主版本（独立 formula）----
        if (f.VersionedFormulae.Count > 0)
        {
            Dictionary<string, FormulaSnapshot> vDetails;
            try
            {
                // 一次批量查询拿到它们各自的 stable 版本号
                vDetails = await FetchFormulaDetailsAsync(f.VersionedFormulae, ct).ConfigureAwait(false);
            }
            catch (ProcessException)
            {
                vDetails = new Dictionary<string, FormulaSnapshot>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var vname in f.VersionedFormulae)
            {
                // 查不到详情时退回从名字尾部取版本号（python@3.11 → 3.11），仍比空着更有信息量
                var vver = vDetails.TryGetValue(vname, out var vf) && !string.IsNullOrWhiteSpace(vf.Stable)
                    ? vf.Stable!
                    : SuffixVersion(vname) ?? vname;

                list.Add(new PackageVersion
                {
                    Version = vver,
                    Note = $"独立 formula「{vname}」：同族的另一主版本，需单独安装（brew install {vname}）。",
                });
            }
        }

        // ---- HEAD（开发分支）----
        if (!string.IsNullOrWhiteSpace(f.Head))
        {
            list.Add(new PackageVersion
            {
                Version = f.Head!,
                IsPrerelease = true,
                Note = "源码仓库的默认分支最新提交，随上游变动、不保证稳定（brew install --HEAD " + name + "）。",
            });
        }

        if (list.Count == 0)
            throw new ProcessException($"找不到 formula {name}", "package_not_found", "", 0);

        // 刻意不按版本号字符串重排：stable 在前，其后保持 brew 给出的主版本顺序（降序），
        // 避免 "3.9" 与 "3.14" 这种跨主版本字符串排序得出误导性结果。
        return list;
    }

    /// <summary>从 versioned formula 名里取版本后缀：python@3.11 → 3.11；没有 @ 返回 null。</summary>
    private static string? SuffixVersion(string formula)
    {
        var at = formula.IndexOf('@');
        if (at < 0 || at == formula.Length - 1) return null;
        var suffix = formula[(at + 1)..].Trim();
        return suffix.Length == 0 ? null : suffix;
    }

    // ---------- 依赖 ----------

    /// <summary>
    /// 直接依赖。brew 的 formula 定义里：
    ///   dependencies[]        → 运行时依赖（IsDev = false）
    ///   build_dependencies[]  → 构建期依赖（IsDev = true，如 wget 的 pkgconf）
    /// Requirement 一律用 "*"：formula 不声明版本区间，版本由 formula 名本身隐含
    /// （例如 openssl@3），依赖始终解析到该 formula 当前的 stable。
    ///
    /// version 参数在这里**不参与查询**：Homebrew 仓库只保留当前 stable 的 formula 定义，
    /// 拿不到"某个历史版本"的依赖集合；若传的是版本化 formula 名（python@3.11），本身就是独立 formula。
    /// </summary>
    public override async Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];

        // 依赖查询失败按"无依赖"处理（基类语义），不抛异常打断界面
        var details = await FetchFormulaDetailsAsync([name], ct).ConfigureAwait(false);
        if (!details.TryGetValue(name, out var f)) return [];

        var list = new List<DependencyRef>(f.Dependencies.Count + f.BuildDependencies.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in f.Dependencies)
        {
            if (d.Length == 0 || !seen.Add(d)) continue;
            list.Add(new DependencyRef(d, "*", false));
        }
        foreach (var d in f.BuildDependencies)
        {
            if (d.Length == 0 || !seen.Add(d)) continue;   // 同时出现在运行时依赖里的不再重复列出
            list.Add(new DependencyRef(d, "*", true));
        }
        return list;
    }

    // ---------- 安装步骤（进度界面按下载方式分行显示的依据） ----------

    /// <summary>
    /// 安装计划：按**下载方式**拆成子项，供进度界面逐行显示。
    /// 对应真实执行路径：`brew install &lt;name&gt;` 会先读 formula 清单与 bottle 元数据，
    /// 然后下载匹配本机 OS/架构的 **bottle**（预编译二进制包），最后解包写入 Cellar 并 link 到 prefix。
    ///
    /// 没有可用 bottle 的 formula（bottle=false，或本机 OS/架构没有对应 bottle）会**退化为源码编译**：
    /// 先拉源码 tarball 再本地编译，所以额外给出一行 SourceBuild 作为可选分支 ——
    /// 它与 BottleDownload 是**二选一**，权重记 0，不参与父进度加权，
    /// 这样"实际会跑的 3 步"权重之和恰好是 1.0，进度条不会卡在 100% 以下。
    ///
    /// version 参数只影响"装哪个版本"：Homebrew 只能装当前 stable（要装旧主版本得用 name 里的
    /// 版本化 formula，如 python@3.11），唯一的例外是 version = "HEAD"——
    /// 此时 Homebrew 不做 bottle 而是从源码构建，所以这里把 BottleDownload 与 SourceBuild 的主次对调。
    /// </summary>
    public override Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var isHead = !string.IsNullOrWhiteSpace(version)
                     && version.Trim().Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<InstallStep> steps =
        [
            new InstallStep
            {
                Title = $"拉取 {name} 的 formula 清单与 bottle 元数据",
                Method = DownloadMethod.ManifestFetch,
                Weight = 0.15,
            },
            new InstallStep
            {
                Title = isHead
                    ? "（本计划用不到）下载 Bottle：安装 HEAD 版本时 Homebrew 直接走源码"
                    : $"下载 {name} 的 Bottle（预编译二进制包）",
                Method = DownloadMethod.BottleDownload,
                Weight = isHead ? 0.0 : 0.60,
            },
            new InstallStep
            {
                Title = $"{name} 写入 Cellar 并链接到安装前缀（brew link）",
                Method = DownloadMethod.LinkWrite,
                Weight = 0.25,
            },
            new InstallStep
            {
                // 可选分支：普通安装时仅当上一步没有可用 bottle 才执行；装 HEAD 时它是主路径
                Title = isHead
                    ? $"从源码构建 HEAD 版本 {name}"
                    : $"（可选）没有匹配的 Bottle 时，改为源码编译 {name}",
                Method = DownloadMethod.SourceBuild,
                Weight = isHead ? 0.60 : 0.0,
            },
        ];
        return Task.FromResult(steps);
    }

    // ---------- 内部工具 ----------

    /// <summary>
    /// 批量查询 formula 详情：`brew info --json=v2 &lt;n1&gt; &lt;n2&gt; …`。
    ///
    /// 实测要点：
    ///   · 支持一次传多个名字，顶层结构是 { "formulae": [ … ] }；
    ///   · **只要有一个名字不存在，整条命令就失败**（退出码 1、stdout 完全为空、stderr 报
    ///     `Error: No available formula with the name "x"`），所以调用方必须能接受空结果；
    ///   · throwOnFailure = true 时把失败转成带机器可读码的异常（单包查询用），
    ///     false 时静默返回空表（搜索降级、依赖查询用）。
    /// </summary>
    private async Task<Dictionary<string, FormulaSnapshot>> FetchFormulaDetailsAsync(
        IReadOnlyList<string> names, CancellationToken ct, bool throwOnFailure = false)
    {
        var map = new Dictionary<string, FormulaSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return map;

        var args = new List<string> { "info", "--json=v2" };
        args.AddRange(names);

        ProcessOutput output;
        try
        {
            output = await RunCliAsync(args, null, null, ct).ConfigureAwait(false);
        }
        catch (ProcessException) when (!throwOnFailure)
        {
            return map;   // CLI 缺失 / 无法启动 → 静默降级
        }

        // 取消时进程被 kill（退出码为信号值），先如实抛取消，别把取消说成命令失败
        ct.ThrowIfCancellationRequested();

        if (!output.Success)
        {
            if (throwOnFailure)
            {
                if (IsFormulaNotFound(output.StdErr, output.StdOut))
                    throw new ProcessException(
                        $"找不到 formula {string.Join(' ', names)}", "package_not_found", output.StdErr, output.ExitCode);
                output.EnsureSuccess($"brew info --json=v2 {string.Join(' ', names)}");
            }
            return map;
        }

        using var doc = ParseJson<JsonDocument>(output.StdOut, "brew info --json=v2");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        if (!doc.RootElement.TryGetProperty("formulae", out var formulae)
            || formulae.ValueKind != JsonValueKind.Array)
            return map;

        // 立刻把需要的字段抄进快照：JsonElement 依附于 using 中的 JsonDocument，出作用域即失效。
        foreach (var f in formulae.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            var snap = Snapshot(f);
            if (snap.Name.Length == 0) continue;
            map[snap.Name] = snap;
            if (!string.IsNullOrEmpty(snap.FullName)) map[snap.FullName!] = snap;
        }
        return map;
    }

    /// <summary>把 formula 的 JSON 对象抄成不依赖 JsonDocument 生命周期的快照。</summary>
    private static FormulaSnapshot Snapshot(JsonElement f)
    {
        var versions = Prop(f, "versions");
        return new FormulaSnapshot(
            Name: Str(f, "name") ?? "",
            FullName: Str(f, "full_name"),
            Desc: Str(f, "desc"),
            Homepage: Str(f, "homepage"),
            License: Str(f, "license"),
            Stable: Str(versions, "stable"),
            Head: Str(versions, "head"),
            Dependencies: StrList(f, "dependencies"),
            BuildDependencies: StrList(f, "build_dependencies"),
            VersionedFormulae: StrList(f, "versioned_formulae"),
            InstalledVersions: InstalledVersions(f),
            Deprecated: Bool(f, "deprecated"),
            DeprecationReason: Str(f, "deprecation_reason"),
            Disabled: Bool(f, "disabled"),
            DisableReason: Str(f, "disable_reason"),
            Outdated: Bool(f, "outdated"));
    }

    /// <summary>installed 是对象数组（[{ "version": "3.6.3", … }]），取其中的 version 字段。</summary>
    private static List<string> InstalledVersions(JsonElement f)
    {
        var list = new List<string>();
        var installed = Prop(f, "installed");
        if (installed.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in installed.EnumerateArray())
        {
            var v = Str(item, "version");
            if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
        }
        return list;
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static bool Bool(JsonElement e, string name)
    {
        var v = Prop(e, name);
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
    }

    private static List<string> StrList(JsonElement e, string name)
    {
        var list = new List<string>();
        var v = Prop(e, name);
        if (v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }

    /// <summary>
    /// brew 的"找不到"文案与其它包管理器不同，这里补上 Homebrew 自己的说法，
    /// 再叠加基类的通用判断（npm 风格文案），最后归一化成 package_not_found。
    /// </summary>
    private static bool IsFormulaNotFound(string stderr, string stdout)
    {
        var s = (stderr + "\n" + stdout).ToLowerInvariant();
        return s.Contains("no formulae or casks found")   // brew search 无结果
            || s.Contains("no available formula")          // brew info 名字不存在
            || s.Contains("no formula found")
            || LooksLikeNotFound(stderr, stdout);
    }

    /// <summary>
    /// 并行取官方安装量，最多 <see cref="MaxEnrichConcurrency"/> 路并发
    /// （SemaphoreSlim 只做异步等待，不阻塞线程）。任何一条失败都不影响其它条目。
    /// </summary>
    private static async Task<Dictionary<string, long>> FetchInstallCountsAsync(
        IReadOnlyList<string> names, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        if (ProcessRunner.Which("curl") is null) return result;   // 没有 curl 就不查，留空而不是编数字

        using var gate = new SemaphoreSlim(MaxEnrichConcurrency);
        var tasks = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(async name =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return (Name: name, Count: await FetchOfficialInstallCountAsync(name, ct).ConfigureAwait(false));
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();

        foreach (var (name, count) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (count is long c && c > 0) result[name] = c;
        }
        return result;
    }

    /// <summary>
    /// 官方安装量：GET https://formulae.brew.sh/api/formula/&lt;name&gt;.json 的
    /// analytics.install.30d（对象，形如 {"wget":9306,"wget --HEAD":28}）。
    /// 非 GitHub 域名，无需 gh-proxy 前缀；`-f` 让 404 变成非零退出码（该站 404 返回 HTML）。
    /// 网络失败、被墙、formula 不在统计里 → 返回 null（**留空而不编造**）。
    /// </summary>
    private static async Task<long?> FetchOfficialInstallCountAsync(string formula, CancellationToken ct)
    {
        try
        {
            var output = await ProcessRunner.RunAsync(
                new ProcessSpec
                {
                    Program = "curl",
                    Args = ["-fsSL", "--max-time", "15", AnalyticsUrl(formula)],
                    TimeoutMs = 20_000,
                }, null, ct).ConfigureAwait(false);

            if (!output.Success)
            {
                ct.ThrowIfCancellationRequested();
                return null;   // 404 / 网络不通 / 该 formula 不在统计里 → 留空
            }

            using var doc = ParseJson<JsonDocument>(output.StdOut, "formulae.brew.sh 安装量");
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var buckets = Prop(Prop(doc.RootElement, "analytics"), "install");
            var window = Prop(buckets, "30d");
            if (window.ValueKind != JsonValueKind.Object) return null;

            // 优先精确匹配 <name> 键；否则把以 "<name> " 开头的键求和（例如 "wget --HEAD"）
            if (window.TryGetProperty(formula, out var exact) && exact.ValueKind == JsonValueKind.Number
                && exact.TryGetInt64(out var n))
            {
                return n;
            }

            long sum = 0;
            var matched = false;
            foreach (var p in window.EnumerateObject())
            {
                if (!p.Name.StartsWith(formula + " ", StringComparison.Ordinal)) continue;
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt64(out var v)) continue;
                sum += v;
                matched = true;
            }
            return matched ? sum : null;
        }
        catch (OperationCanceledException)
        {
            throw;   // 用户取消要如实向上传播（超时由 ProcessRunner 归一化为 TimedOut，不走这里）
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>安装量接口地址。formula 名里的 @ 等字符做转义（服务端可正常解码，已实测）。</summary>
    private static string AnalyticsUrl(string formula) =>
        $"https://formulae.brew.sh/api/formula/{Uri.EscapeDataString(formula)}.json";

    /// <summary>
    /// formula 详情快照。字段名与 `brew info --json=v2` 的 snake_case 字段一一对应，
    /// 抽出来是为了摆脱 JsonDocument 的生命周期约束，也让调用点不必反复 TryGetProperty。
    /// </summary>
    private sealed record FormulaSnapshot(
        string Name,
        string? FullName,
        string? Desc,
        string? Homepage,
        string? License,
        string? Stable,
        string? Head,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<string> BuildDependencies,
        IReadOnlyList<string> VersionedFormulae,
        IReadOnlyList<string> InstalledVersions,
        bool Deprecated,
        string? DeprecationReason,
        bool Disabled,
        string? DisableReason,
        bool Outdated);
}
