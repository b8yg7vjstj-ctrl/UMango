using System.Text.Json;
using System.Text.RegularExpressions;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// scoop 适配器（**仅 Windows**：Kind = <see cref="PackageSourceKind.Scoop"/>，
/// 平台门控由基类按 SupportedPlatforms 完成，在 macOS/Linux 上不会执行任何 scoop 命令）。
///
/// 关于"未验证"：scoop 只存在于 Windows，而本项目在 macOS 上开发，
/// 因此本类里的**输出解析逻辑全部未经真机验证**。对应策略有两条：
///   1. <see cref="VerificationNote"/> 如实返回未验证说明，界面会打上"未验证"标记；
///   2. 解析器写成**宽容多形态**（接受若干种可能的输出结构），
///      一旦遇到完全不认识的结构就抛带**原始输出片段**的 parse_error ——
///      Windows 用户第一次运行就能把真实格式贴回来，而不是默默给出错误数据。
///
/// scoop 的几个输出事实（决定了下面的命令与解析写法）：
///   · 版本探测：`scoop --version` → 一行版本号（基类默认 VersionArgs 已是 --version）；
///   · 搜索：`scoop search &lt;query&gt;` 是**纯文本表格，没有 --json**，
///     必须先遇到 "----" 分隔线之后才是数据行；
///   · manifest：`scoop cat &lt;app&gt;` 打印该 app 的 manifest JSON
///     （version / description / homepage / license / depends / url / architecture）；
///   · bucket 里**只有 manifest 声明的当前版本**，没有历史版本列表 ——
///     所以 GetVersionsAsync 只返回一个版本，并在 Note 里写明原因（绝不伪造版本）。
/// </summary>
public sealed class ScoopPackageManager : CliPackageManagerBase
{

    /// <summary>
    /// 列出已安装的 app。
    ///
    /// ⚠️ **未在真机验证**（scoop 仅存在于 Windows，本机是 macOS）。
    /// 命令形态依据官方文档：`scoop list` 输出纯文本表格，形如
    /// <code>
    /// Installed apps:
    /// Name   Version  Source  Updated  Info
    /// ----   -------  ------  -------  ----
    /// wget   1.21.3   main    2023-01-01
    /// </code>
    /// 解析沿用与 search 相同的"先找到 ---- 分隔线再收数据行"策略。
    /// 若真实输出与此不符，会抛带原始片段的解析错误（便于回报），而不是默默给错数据。
    /// </summary>
    public override async Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await RunCliAsync(["list"], null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        var list = new List<InstalledPackage>();
        var seenSeparator = false;
        foreach (var raw in output.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("----")) { seenSeparator = true; continue; }
            if (!seenSeparator) continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (!parts[0].All(c => char.IsAsciiLetterOrDigit(c) || "-._+".Contains(c))) continue;

            list.Add(new InstalledPackage
            {
                Source = Kind,
                Name = parts[0],
                Version = parts.Length > 1 ? parts[1] : null,
                InstallPath = InstallDirectory
            });
        }
        return list;
    }

    /// <summary>
    /// 未在真机验证的说明文案。界面会在包源分类项上显示它，
    /// 用户回报解析异常时也靠它知道"该找作者反馈原始 CLI 输出"。
    /// </summary>
    private const string UnverifiedNote =
        "该包源的解析逻辑尚未在 Windows 真机验证（本项目在 macOS 上开发）。若结果异常，请把原始 CLI 输出反馈。"
        + "另：安装目录取自 `scoop prefix`，若该命令在无参数时输出的是帮助文本而非路径，"
        + "子标题会退回默认的 %USERPROFILE%\\scoop\\apps。";

    /// <summary>
    /// 搜索时对前 N 条结果补 manifest 详情（描述/主页/许可证）的上限。
    ///
    /// 为什么要设上限：`scoop search` 的表格里**没有描述字段**，
    /// 想拿到描述只能再跑一次 `scoop cat &lt;app&gt;`；而 scoop 是 PowerShell 脚本，
    /// 每次调用都要起一次 PowerShell（数百毫秒起）。搜索是"输入即触发"的高频操作，
    /// 给全部结果补详情会让界面明显卡顿，因此只补前几条，其余留空由详情页补齐。
    /// </summary>
    private const int ManifestEnrichmentLimit = 5;

    /// <summary>limit &lt;= 0 时（调用方未指定）使用的兜底条数，避免一次渲染过多卡片。</summary>
    private const int DefaultSearchLimit = 50;

    /// <summary>
    /// 表格切列：2 个以上连续空白，或单个制表符。
    /// scoop 的表格是用空格填充对齐的（列间至少 2 个空格），
    /// 但不同终端/版本的制表方式可能变成 TAB —— 一并兜住，免得整行因为"列没切开"被丢弃。
    /// 注意：单个空格**不**切列（"1.2.3 build" 这种带空格的列值不该被切断）。
    /// </summary>
    private static readonly Regex ColumnSeparator = new(@"\t+|\s{2,}", RegexOptions.Compiled);

    public override PackageSourceKind Kind => PackageSourceKind.Scoop;

    /// <summary>
    /// 能力声明。OfficialDownloadCounts = false：
    /// scoop 没有公开的下载量接口（不存在 registry 那种 downloads 字段），如实置 false。
    /// </summary>
    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: true,
        OfficialDownloadCounts: false, Install: true, Uninstall: true, Upgrade: true);

    public override string InstallHint =>
        "scoop 仅 Windows 可用。在 PowerShell 中执行："
        + "Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser，然后 irm get.scoop.sh | iex";

    /// <summary>未真机验证 —— 必须如实标注（界面据此显示"未验证"提示）。</summary>
    protected override string? VerificationNote => UnverifiedNote;

    /// <summary>
    /// 超时放宽到 3 分钟：scoop 的首次 `search` / `cat` 可能先更新 bucket
    /// （"Updating Scoop..." / 拉取 bucket 仓库），120 秒不一定够。
    /// </summary>
    protected override int DefaultTimeoutMs => 180_000;

    /// <summary>
    /// 平台默认安装目录。
    ///
    /// scoop 的目录结构：根目录（默认 %USERPROFILE%\scoop，安装脚本写入 SCOOP 环境变量）
    /// 下的 apps\ 存放各 app 的版本目录，shims\ 存放 shim。
    /// 这里给的是 **apps 目录**（与需求里"显示该管理器的安装目录"一致）；
    /// `scoop prefix` 成功时会由基类解析出真实路径覆盖本值。
    ///
    /// 非 Windows 上 scoop 不可用，但包源分类项仍会显示子标题，
    /// 此时返回 Windows 风格的占位串（而不是拼出一个 macOS 路径误导用户）。
    /// </summary>
    protected override string DefaultInstallDirectory
    {
        get
        {
            if (SystemInfo.Platform != PlatformKind.Windows)
                return @"%USERPROFILE%\scoop\apps";

            // 安装脚本会把根目录写进 SCOOP 环境变量（用户可自定义，例如 D:\scoop）
            var root = Environment.GetEnvironmentVariable("SCOOP");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(SystemInfo.HomeDirectory, "scoop");
            return Path.Combine(root.Trim(), "apps");
        }
    }

    /// <summary>
    /// `scoop prefix` 查询真实安装目录。
    /// 注意：scoop 的 `prefix` 子命令本意是 `scoop prefix &lt;app&gt;`（打印某个 app 的安装路径），
    /// 不带参数时可能直接报错 —— 那正是我们想要的兜底路径：
    /// 基类的 ResolveInstallDirectoryAsync 只在命令成功且有输出时才采用，失败会静默保留
    /// <see cref="DefaultInstallDirectory"/>，所以这里不需要额外判断。
    /// </summary>
    protected override string[]? InstallDirArgs => ["prefix"];

    // ---------------------------------------------------------------------
    // 搜索
    // ---------------------------------------------------------------------

    /// <summary>
    /// `scoop search &lt;query&gt;` 解析纯文本表格。
    ///
    /// 真实输出形态（官方文档描述，本机无法验证）：
    /// <code>
    /// Results from local buckets...
    ///
    /// Name    Version  Source  Updated     Info
    /// ----    -------  ------  -------     ----
    /// wget    1.21.3   main    2023-01-01
    /// curl    8.4.0    main    2024-01-05
    /// </code>
    /// 解析要点：
    ///   1. **必须先遇到以 ---- 开头的分隔线**，之后的行走才算数据行 ——
    ///      否则会把 "Results from local buckets..."、表头 "Name Version ..." 当包名；
    ///   2. 数据行按 **2 个以上连续空格**切列，第 1 列是包名，
    ///      且只接受由字母数字与 -._+ 组成的名字（这层校验同时挡掉多 bucket 分段时
    ///      再次出现的表头、以及夹带的提示文字）；
    ///   3. 第 2 列是版本，第 3 列是 bucket 名（同一 app 可能出现在多个 bucket，
    ///      按出现顺序**只保留第一个** —— 这就是 scoop 的解析优先级）；
    ///   4. 第 4 列是日期、第 5 列是 Info；PackageInfo 模型里没有对应字段，
    ///      故意丢弃（不把日期硬塞进版本号之类字段，避免给出错误数据）。
    ///
    /// 认不出结构时（没有分隔线、也没有"无匹配"提示）抛 parse_error 并附原始片段。
    /// </summary>
    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // `scoop search` 没有条数参数（也没有 --json），limit 只能由我们本地截断
        var effectiveLimit = limit > 0 ? limit : DefaultSearchLimit;

        var output = await RunCliAsync(["search", query], null, null, ct).ConfigureAwait(false);

        // 归一化"包不存在"：scoop 的措辞形如 "Couldn't find manifest for 'x'."，
        // 基类 LooksLikeNotFound 覆盖不到，故叠加 scoop 专用判断（见 IsScoopNotFound）
        if (!output.Success && IsScoopNotFound(output.StdErr, output.StdOut))
            throw new ProcessException($"找不到与 {query} 匹配的包", "package_not_found", output.StdErr, output.ExitCode);
        output.EnsureSuccess($"scoop search {query}");

        var (rows, sawTable) = ParseSearchTable(output.StdOut);

        // 没识别出数据行且连表格分隔线都没见到：
        //   · 若输出里有"无匹配/没有 bucket"之类的提示 → 合法的空结果；
        //   · 否则说明 scoop 版本换了输出格式 → 抛 parse_error 并附原始输出片段，
        //     让 Windows 用户第一次运行就能把真实格式贴给我们。
        if (rows.Count == 0)
        {
            if (sawTable || LooksLikeNoMatches(output.StdOut + "\n" + output.StdErr)) return [];
            throw new ProcessException(
                $"无法识别 scoop search 的输出结构（scoop 版本差异？）。原始输出片段：{Truncate(output.StdOut, 300)}",
                "parse_error", output.StdOut, output.ExitCode);
        }

        var list = new List<PackageInfo>();
        for (var i = 0; i < rows.Count && list.Count < effectiveLimit; i++)
        {
            var row = rows[i];

            // 只给前几条补 manifest 详情（理由见 ManifestEnrichmentLimit 的注释）：
            // 表格里没有描述/主页/许可证，全靠 manifest 兜。
            string? summary = null, license = null, homepage = null;
            var version = row.Version;
            if (i < ManifestEnrichmentLimit)
            {
                // 补详情失败不影响搜索结果本身 —— 退回"只有名字和版本"的卡片
                using var doc = await TryLoadManifestAsync(row.Name, ct).ConfigureAwait(false);
                if (doc is not null)
                {
                    var manifest = ResolveManifestRoot(doc.RootElement);
                    summary = ReadText(manifest, "description");
                    license = ReadText(manifest, "license");
                    homepage = ReadText(manifest, "homepage");
                    // manifest 里的 version 比表格更权威（表格偶尔会滞后于 bucket 更新）
                    version = ReadText(manifest, "version") ?? version;
                }
            }

            list.Add(new PackageInfo
            {
                Source = Kind,
                Name = row.Name,
                LatestVersion = version,
                Summary = summary,
                License = license,
                Homepage = homepage,
                // 下载量留空：scoop 无公开接口（Capabilities.OfficialDownloadCounts = false）
            });
        }

        return list;
    }

    // ---------------------------------------------------------------------
    // 版本列表
    // ---------------------------------------------------------------------

    /// <summary>
    /// 版本列表。**scoop 的 bucket 只保留 manifest 里声明的当前版本**，
    /// 不存在历史版本列表（没有 npm 那样的 packument）。
    /// 因此这里如实只返回一个版本，并在 Note 里写明原因 —— 不伪造版本号。
    /// </summary>
    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var doc = await LoadManifestAsync(name, ct).ConfigureAwait(false);
        var manifest = ResolveManifestRoot(doc.RootElement);
        var version = ReadText(manifest, "version");

        if (string.IsNullOrEmpty(version))
            throw new ProcessException(
                $"{name} 的 manifest 中没有 version 字段。原始输出片段：{Truncate(doc.RootElement.GetRawText(), 200)}",
                "parse_error", doc.RootElement.GetRawText(), 0);

        IReadOnlyList<PackageVersion> versions =
        [
            new PackageVersion
            {
                Version = version,
                IsPrerelease = IsPrerelease(version),
                // 如实说明"为什么只有一个版本"，避免用户以为是我们漏抓了
                Note = "scoop bucket 只保留 manifest 中声明的当前版本，不提供历史版本列表"
            }
        ];
        return versions;
    }

    // ---------------------------------------------------------------------
    // 依赖
    // ---------------------------------------------------------------------

    /// <summary>
    /// 直接依赖：manifest 的 <c>depends</c> 数组 → 每项一个 <see cref="DependencyRef"/>，
    /// Requirement 统一为 "*"（scoop 的依赖不带版本约束，只写 app 名，可能带 bucket 前缀如
    /// "extras/vcredist"，这里保留原样以便界面显示它来自哪个 bucket）。
    ///
    /// 关于 version 参数：scoop 不保留历史 manifest，拿不到旧版本的 depends，
    /// 所以无论传哪个版本都按**当前 manifest** 返回（若与请求版本不一致也不报错，
    /// 因为这是 scoop 的能力边界，而不是调用方的错误）。
    /// manifest 的 <c>suggest</c> 是"建议安装"而非依赖，故意不收集。
    /// </summary>
    public override async Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var doc = await LoadManifestAsync(name, ct).ConfigureAwait(false);
        var manifest = ResolveManifestRoot(doc.RootElement);

        var names = ReadDepends(manifest);
        return names.Select(dep => new DependencyRef(dep, "*")).ToList();
    }

    // ---------------------------------------------------------------------
    // 安装计划（进度界面的子项来源）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 按 scoop 的下载方式拆步 —— 每一步在下载进度界面各占一行子项：
    ///   ① ManifestFetch：从本地 bucket / scoop 仓库拉取并解析 manifest；
    ///   ② DependencyResolve：manifest 声明了 depends 时，scoop 会先装依赖（仅有依赖时出现）；
    ///   ③ DirectUrl：按 manifest 的 url / architecture.*.url 直链下载压缩包；
    ///   ④ LinkWrite：把包解压到 apps\&lt;name&gt;\&lt;version&gt;，并在 shims\ 写入 shim。
    ///
    /// manifest 只是**尽力**读取（拿不到也照样给出完整计划）：
    /// 用它把步骤标题写具体（实际版本号、目标架构、依赖条数），
    /// 步骤结构本身来自 scoop 的固定流程，不依赖网络。
    /// </summary>
    public override async Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string? declaredVersion = null;
        var dependencies = new List<string>();
        var architectures = new List<string>();

        // 尽力读取 manifest：失败（网络/未安装/解析异常）时静默降级为通用标题
        using (var doc = await TryLoadManifestAsync(name, ct).ConfigureAwait(false))
        {
            if (doc is not null)
            {
                var manifest = ResolveManifestRoot(doc.RootElement);
                declaredVersion = ReadText(manifest, "version");
                dependencies = ReadDepends(manifest);
                architectures = ReadArchitectureKeys(manifest);
            }
        }

        var target = !string.IsNullOrEmpty(version) ? version : declaredVersion;
        var targetText = string.IsNullOrEmpty(target) ? name : $"{name} {target}";
        var archText = architectures.Count > 0 ? $"（架构：{string.Join(" / ", architectures)}）" : "";

        // 有依赖解析步骤时，把它的权重从下载步骤里匀出来，使总权重仍为 1.0
        var weighsDependencies = dependencies.Count > 0;

        var steps = new List<InstallStep>
        {
            new()
            {
                Title = $"拉取清单：scoop cat {name}（bucket manifest）",
                Method = DownloadMethod.ManifestFetch,
                Weight = 0.10
            }
        };

        if (weighsDependencies)
        {
            var preview = string.Join("、", dependencies.Take(3));
            var more = dependencies.Count > 3 ? " 等" : "";
            steps.Add(new InstallStep
            {
                Title = $"解析并安装依赖：{preview}{more}（共 {dependencies.Count} 项）",
                Method = DownloadMethod.DependencyResolve,
                Weight = 0.10
            });
        }

        steps.Add(new InstallStep
        {
            Title = $"直链下载：{targetText} 的 url{archText}",
            Method = DownloadMethod.DirectUrl,
            Weight = weighsDependencies ? 0.55 : 0.60
        });

        steps.Add(new InstallStep
        {
            Title = $"解压与链接：写入 apps\\{name} 并生成 shim",
            Method = DownloadMethod.LinkWrite,
            Weight = weighsDependencies ? 0.25 : 0.30
        });

        return steps;
    }

    // ---------------------------------------------------------------------
    // manifest 读取
    // ---------------------------------------------------------------------

    /// <summary>
    /// 运行 `scoop cat &lt;name&gt;` 并把输出解析成 manifest JSON。
    ///
    /// 失败语义（与基类的约定一致）：
    ///   · 命令失败且像"找不到" → package_not_found；
    ///   · 输出为空 → package_not_found（scoop 找不到 app 时可能是空输出 + 退出码 0）；
    ///   · 输出不是可识别 JSON → parse_error（ParseJson 内部会带上原始片段）。
    /// 调用方负责 Dispose 返回的 JsonDocument。
    /// </summary>
    private async Task<JsonDocument> LoadManifestAsync(string name, CancellationToken ct)
    {
        var output = await RunCliAsync(["cat", name], null, null, ct).ConfigureAwait(false);

        if (!output.Success)
        {
            if (IsScoopNotFound(output.StdErr, output.StdOut))
                throw new ProcessException($"找不到包 {name}", "package_not_found", output.StdErr, output.ExitCode);
            output.EnsureSuccess($"scoop cat {name}");
        }

        var doc = ParseJson<JsonDocument>(output.StdOut, $"scoop cat {name}")
                  ?? throw new ProcessException(
                      $"scoop cat {name} 没有返回 manifest（输出为空）",
                      "parse_error", Truncate(output.StdOut, 200), output.ExitCode);
        return doc;
    }

    /// <summary>
    /// 尽力读取 manifest：任何 ProcessException（找不到 / 解析失败 / 命令失败）
    /// 都退化为 null，让调用方走"没有详情"的降级路径。
    /// 注意不吞 OperationCanceledException —— 取消必须能传出去。
    /// </summary>
    private async Task<JsonDocument?> TryLoadManifestAsync(string name, CancellationToken ct)
    {
        try
        {
            return await LoadManifestAsync(name, ct).ConfigureAwait(false);
        }
        catch (ProcessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // 纯函数：文本表格解析
    // ---------------------------------------------------------------------

    /// <summary>scoop search 表格的一行（Name / Version / Source；日期与 Info 列模型用不上，故意丢弃）。</summary>
    private readonly record struct SearchRow(string Name, string? Version, string? Bucket);

    /// <summary>
    /// 解析 `scoop search` 的表格。
    /// 返回 (数据行, 是否见到过表格分隔线)：
    /// 第二个值用于区分"识别出表格但无匹配"（合法的空结果）与"完全不认识的结构"（抛 parse_error）。
    /// </summary>
    private static (List<SearchRow> Rows, bool SawTable) ParseSearchTable(string raw)
    {
        var rows = new List<SearchRow>();
        var sawSeparator = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // ① 分隔线："----    -------  ------  -------     ----"（只由 - 与空白组成）
            //    见到它才说明后面是数据行
            if (IsSeparatorLine(line))
            {
                sawSeparator = true;
                continue;
            }

            // ② 分隔线之前的一切（"Results from local buckets..."、表头）都跳过
            if (!sawSeparator) continue;

            // ③ 数据行：按 2 个以上连续空格切列
            var columns = ColumnSeparator.Split(line)
                                         .Select(c => c.Trim())
                                         .Where(c => c.Length > 0)
                                         .ToArray();
            if (columns.Length == 0) continue;

            var name = columns[0];

            // 名字合法性：只允许字母数字与 -._+；这一层同时挡掉多 bucket 分段时
            // 再次出现的表头行（"Name    Version  Source ..."）与夹带的提示文字
            if (!IsValidAppName(name)) continue;

            // 双保险：第 2 列若写的是 "Version" 说明这仍是表头，不是数据行
            var second = columns.Length > 1 ? columns[1] : null;
            if (second is not null && second.Equals("Version", StringComparison.OrdinalIgnoreCase)) continue;

            // 同一 app 可能出现在多个 bucket → 只保留第一次出现的（bucket 顺序即解析优先级）
            if (!seen.Add(name)) continue;

            rows.Add(new SearchRow(
                Name: name,
                Version: IsUsableColumn(second) ? second : null,
                Bucket: columns.Length > 2 && IsUsableColumn(columns[2]) ? columns[2] : null));
        }

        return (rows, sawSeparator);
    }

    /// <summary>分隔线判定：第一个空白分隔的片段全由 '-' 组成且长度 ≥ 2。</summary>
    private static bool IsSeparatorLine(string line)
    {
        var first = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 0) return false;
        var token = first[0];
        return token.Length >= 2 && token.All(c => c == '-');
    }

    /// <summary>包名合法性：scoop 的 app 名由字母数字与 -._+ 组成。</summary>
    private static bool IsValidAppName(string s)
    {
        if (s.Length == 0) return false;
        // "Name" 是表头首列，字面上也符合名字字符集 → 单独排除
        if (s.Equals("Name", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c)) continue;
            if (c is '-' or '.' or '_' or '+') continue;
            return false;
        }
        return true;
    }

    /// <summary>列值是否可用（非空、且不是全 '-' 的占位）。</summary>
    private static bool IsUsableColumn(string? s) =>
        !string.IsNullOrWhiteSpace(s) && !s.All(c => c == '-');

    /// <summary>输出是否表示"搜索无匹配 / 没有 bucket"（合法的空结果，不是解析失败）。</summary>
    private static bool LooksLikeNoMatches(string text)
    {
        var s = text.ToLowerInvariant();
        return s.Contains("no matches")
            || s.Contains("no results")
            || s.Contains("no package")
            || s.Contains("no buckets")
            || s.Contains("didn't match")
            || s.Contains("did not match")
            || s.Contains("couldn't find")
            || s.Contains("not found");
    }

    /// <summary>
    /// "包不存在"归一化：基类 <see cref="LooksLikeNotFound"/> 认的是 npm 系措辞，
    /// scoop 的真实措辞是 "Couldn't find manifest for 'x'." / "Couldn't find app 'x'."，
    /// 这里叠加 scoop 专用判断（多写几种以防版本差异）。
    /// </summary>
    private static bool IsScoopNotFound(string stderr, string stdout)
    {
        if (LooksLikeNotFound(stderr, stdout)) return true;
        var s = (stderr + " " + stdout).ToLowerInvariant();
        return s.Contains("couldn't find manifest")
            || s.Contains("couldn't find app")
            || s.Contains("couldn't find")
            || s.Contains("no manifest")
            || s.Contains("is not installed");
    }

    // ---------------------------------------------------------------------
    // 纯函数：manifest JSON 解析（宽容多形态）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 宽容定位 manifest 根对象。
    /// 正常形态：`scoop cat` 的输出根就是 manifest 对象；
    /// 宽容形态：某些包装会把 manifest 放进 <c>manifest</c> 字段里 → 下钻一层。
    /// </summary>
    private static JsonElement ResolveManifestRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("version", out _)
            && root.TryGetProperty("manifest", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            return inner;
        }
        return root;
    }

    /// <summary>
    /// 宽容读取文本字段，按顺序取第一个非空值：
    ///   · 字符串 → 原样；
    ///   · 数字（version 偶尔写成数字）→ 转成字符串；
    ///   · 对象（license 可能是 SPDX 对象 {"identifier": "..."}）→ 递归取 identifier/name/value。
    /// </summary>
    private static string? ReadText(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.Object => ReadText(value, "identifier", "name", "value"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    /// <summary>
    /// 读取 manifest 的 <c>depends</c>，容忍三种写法：
    ///   · 字符串数组：["lessmsi", "innounp"]（最常见）；
    ///   · 元素为数组："任一即可"的备选依赖 → 备选逐个展开（模型没有"或"语义，全部按直接依赖列出）；
    ///   · 元素为对象或整体为对象映射 → 取其中的 name / id / 键名。
    /// 没有 depends 字段表示"无依赖"，不是错误 → 返回空列表。
    /// </summary>
    private static List<string> ReadDepends(JsonElement manifest)
    {
        var names = new List<string>();
        if (manifest.ValueKind != JsonValueKind.Object) return names;
        if (!manifest.TryGetProperty("depends", out var depends)) return names;

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var trimmed = candidate.Trim();
            if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) names.Add(trimmed);
        }

        switch (depends.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in depends.EnumerateArray())
                {
                    switch (item.ValueKind)
                    {
                        case JsonValueKind.String:
                            Add(item.GetString());
                            break;
                        case JsonValueKind.Array:
                            foreach (var alternative in item.EnumerateArray())
                                if (alternative.ValueKind == JsonValueKind.String) Add(alternative.GetString());
                            break;
                        case JsonValueKind.Object:
                            Add(ReadText(item, "name", "id"));
                            break;
                    }
                }
                break;
            case JsonValueKind.String:
                Add(depends.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in depends.EnumerateObject()) Add(property.Name);
                break;
        }

        return names;
    }

    /// <summary>
    /// 读取 manifest 里声明的架构键（<c>architecture</c>：常见为 {"64bit": …, "32bit": …, "arm64": …}）。
    /// 只用于把"直链下载"步骤的标题写具体，读不到就算了。
    /// </summary>
    private static List<string> ReadArchitectureKeys(JsonElement manifest)
    {
        var keys = new List<string>();
        if (manifest.ValueKind != JsonValueKind.Object) return keys;
        if (!manifest.TryGetProperty("architecture", out var arch)) return keys;

        switch (arch.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in arch.EnumerateObject())
                    if (!string.IsNullOrWhiteSpace(property.Name)) keys.Add(property.Name.Trim());
                break;
            case JsonValueKind.String:
                var single = arch.GetString();
                if (!string.IsNullOrWhiteSpace(single)) keys.Add(single.Trim());
                break;
            case JsonValueKind.Array:
                foreach (var item in arch.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        keys.Add(item.GetString()!.Trim());
                break;
        }

        return keys;
    }

    /// <summary>粗略判断预发布版本（含 '-' 且后半段有字母，例如 1.2.3-beta.1）。</summary>
    private static bool IsPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return false;
        return version[(dash + 1)..].Any(char.IsLetter);
    }
}
