using System.Globalization;
using System.Text.Json;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// pnpm 适配器。
///
/// 为什么可以大量复用 npm 的解析思路：pnpm 的 registry 交互与 npm **同源** ——
/// 它同样读 registry.npmjs.org 的 packument，`pnpm view` / `pnpm search` 的 JSON 输出与
/// `npm view` / `npm search --json` 同构（pnpm 有意保持兼容，便于脚本迁移）。
/// 因此本适配器只覆盖 pnpm 真正不同的三点：
///   1. 全局目录查询是 `pnpm root -g`，且全局目录位于 PNPM_HOME 之下而非 node_modules 惯例位置；
///   2. 安装过程走"内容寻址存储 + 硬链接"，进度步骤的权重分配与 npm 不同（见 PlanInstallAsync 注释）；
///   3. 没有独立的下载量统计接口 → OfficialDownloadCounts = false。
///
/// 共性能力（平台门控、CLI 定位、版本探测、超时/取消、容错 JSON、package_not_found 归一化）
/// 全部由 <see cref="CliPackageManagerBase"/> 提供，此处不重复实现。
/// </summary>
public sealed class PnpmPackageManager : CliPackageManagerBase
{
    public override PackageSourceKind Kind => PackageSourceKind.Pnpm;


    /// <summary>
    /// 列出全局已安装的包。
    /// **实测格式**：`pnpm list -g --depth=0 --json` 返回**数组**，
    /// 首元素形如 { "path": "~/Library/pnpm/global/v11", "private": true, "dependencies": {...} }。
    /// 注意与 npm 的差别：npm 返回对象，pnpm 返回数组 —— 直接照搬 npm 的解析会拿不到数据。
    /// </summary>
    public override async Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await RunCliAsync(["list", "-g", "--depth=0", "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        using var doc = ParseJson<JsonDocument>(output.StdOut, "pnpm list -g");
        if (doc is null) return [];

        var list = new List<InstalledPackage>();
        IEnumerable<JsonElement> roots = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : new[] { doc.RootElement };

        foreach (var root in roots)
        {
            var installPath = root.TryGetProperty("path", out var pv) ? pv.GetString() : null;
            if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var dep in deps.EnumerateObject())
            {
                var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                list.Add(new InstalledPackage
                {
                    Source = Kind, Name = dep.Name, Version = version, InstallPath = installPath
                });
            }
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 能力声明。
    ///
    /// **OfficialDownloadCounts 必须为 false**：pnpm 自身不提供下载量统计接口。
    /// npm registry 的 downloads API 只统计经 npm 客户端产生的下载，pnpm 的安装不计入其中，
    /// 拿它当"pnpm 的下载量"会给出系统性偏低且来源错误的数字 —— 因此这里如实关闭该位，
    /// 由界面改走 GitHub Stars 兜底（PackageInfo.DownloadCountIsFallback）。
    ///
    /// 其余能力 pnpm 都原生支持：
    ///   · Search             —— `pnpm search <query> --json`（pnpm 11 仍支持）；
    ///   · ListVersions       —— `pnpm view <pkg> --json` 的 versions / dist-tags；
    ///   · ResolveDependencies—— `pnpm view <pkg>[@ver] --json` 的 dependencies 等字段；
    ///   · Install/Uninstall/Upgrade —— `pnpm add -g` / `pnpm remove -g` / `pnpm update -g`。
    /// </summary>
    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: true,
        OfficialDownloadCounts: false, Install: true, Uninstall: true, Upgrade: true);

    /// <summary>
    /// 未安装 pnpm 时的指引。pnpm 依赖 Node.js ≥ 18，故先给 npm 安装与 Corepack 两条路，
    /// 最后附官方独立安装脚本入口（Windows 上可用 winget install pnpm.pnpm）。
    /// </summary>
    public override string InstallHint =>
        "pnpm 需要 Node.js ≥ 18，任选一种装法："
        + "① `npm install -g pnpm`；② 用 Node 自带 Corepack：`corepack enable pnpm`；"
        + "③ Windows 还可 `winget install pnpm.pnpm`；"
        + "④ 官方安装脚本与更多方式见 https://pnpm.io/installation";

    /// <summary>
    /// 平台默认全局安装目录（`pnpm root -g` 解析出真实值之前的兜底展示值）。
    ///
    /// pnpm 的全局目录**不在** node_modules 惯例位置，而是挂在 PNPM_HOME 之下：
    ///   · Windows：%LOCALAPPDATA%\pnpm
    ///   · macOS  ：~/Library/pnpm
    ///   · Linux  ：~/.local/share/pnpm
    /// 真机验证（pnpm 12.4.2 / macOS）：真实全局目录是 &lt;PNPM_HOME&gt;/global/&lt;版本段&gt;，
    /// `pnpm root -g` 当时给出 `/Users/&lt;u&gt;/Library/pnpm/global/v11`。
    /// 该"版本段"由 pnpm 自己决定（既不是 npm 那种固定 node_modules，也不随 pnpm 主版本线性变化），
    /// 而且**在首次全局安装之前这个目录根本不存在**（`pnpm root -g` 只是报出将要使用的路径）。
    /// 因此这里绝不写死版本段，兜底值只到 global 一级，真实值一律问 CLI。
    /// </summary>
    protected override string DefaultInstallDirectory => SystemInfo.Platform switch
    {
        PlatformKind.Windows => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "global"),
        PlatformKind.MacOs => Path.Combine(SystemInfo.HomeDirectory, "Library", "pnpm", "global"),
        _ => Path.Combine(SystemInfo.HomeDirectory, ".local", "share", "pnpm", "global")
    };

    /// <summary>
    /// `pnpm root -g` 是查询全局安装目录最权威的方式（版本段由它算好），
    /// 真机验证 stdout 是**单行绝对路径**（例如 `/Users/x/Library/pnpm/global/v11`），
    /// 正好与基类"取第一条非空 stdout 行"的解析方式吻合。
    ///
    /// 已知限制（真机验证）：当 PNPM_HOME 的 bin 目录不在 PATH 里时，该命令会以退出码 1 报
    /// ERR_PNPM_GLOBAL_BIN_DIR_NOT_IN_PATH 并且 **stdout 为空**（错误只在 stderr）。
    /// 此时基类会因为 !Success 而保留上面的平台默认值 —— 这是刻意的：宁可显示兜底目录，
    /// 也不把一行错误文本当成安装目录显示出来。
    /// </summary>
    protected override string[]? InstallDirArgs => ["root", "-g"];

    /// <summary>
    /// 如实标注验证状态：本适配器已在真机上用 pnpm 12.4.2（macOS）实测 `pnpm --version`、
    /// `pnpm root -g`、`pnpm view`（含版本列表 / dist-tags / time / deprecated / dependencies）、
    /// `pnpm search --json` 与"包不存在"的报错路径，输出形态与解析逻辑一致。
    /// 仍需留意的差异：真机当时连的是镜像 registry，官方 registry 的字段集合若有出入，
    /// 解析会退化为"少显示某几个字段"而不会失败（各字段都是可选读取）。
    /// </summary>
    protected override string? VerificationNote =>
        "已用 pnpm 12.4.2（macOS）真机验证版本探测、安装目录、搜索、版本列表与依赖解析；"
        + "真机使用镜像 registry，官方源字段若有差异只影响可选字段的显示";

    // ---------- 搜索 ----------

    /// <summary>
    /// 搜索：`pnpm search &lt;query&gt; --json`。
    ///
    /// 命令为什么这么写：
    ///   · pnpm 把搜索透传给 registry 的 /-/v1/search 接口，`--json` 输出与
    ///     `npm search --json` 同构，**真机验证确实是一个扁平数组**（该源返回 20 项）：
    ///     [{"name":"left-pad","version":"1.3.0","description":"String left pad",
    ///       "license":"WTFPL","keywords":[...],"date":...} ...]
    ///     该镜像源的项目里没有 links/homepage 字段，所以主页可能为空 —— 这些字段按"可选"读取，
    ///     缺失只是少显示一栏，不会让整次搜索失败。
    ///   · 参数逐项塞进 ProcessRunner 的 ArgumentList（不经过 shell），
    ///     所以查询词里的空格、引号、`@` 都不需要手工转义；
    ///   · pnpm 没有 npm 的 `--searchlimit` 选项，条数全部由本地 Take(limit) 截断，
    ///     这样也避免给不同 pnpm 版本传它不认识的参数。
    /// </summary>
    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var output = await RunCliAsync(["search", query, "--json"], null, null, ct).ConfigureAwait(false);

        if (!output.Success && LooksLikeNotFound(output.StdErr, output.StdOut))
            throw new ProcessException($"找不到与 {query} 匹配的包", "package_not_found", output.StdErr, output.ExitCode);
        output.EnsureSuccess($"pnpm search {query}");

        using var doc = ParseJson<JsonDocument>(output.StdOut, "pnpm search")
                        ?? throw new ProcessException("pnpm search 未返回内容", "parse_error", "", 0);

        var root = doc.RootElement;
        var list = new List<PackageInfo>();

        // 兼容两种外层形状：
        //   A. 与 npm search 同构的扁平数组（pnpm 的常规输出）；
        //   B. registry /-/v1/search 的原始响应 {"objects":[{"package":{...}}],"total":N}，
        //      某些 pnpm 版本或代理会原样透出，这里一并兜住，避免因多一层外壳而整页空结果。
        if (root.ValueKind == JsonValueKind.Array)
        {
            CollectSearchItems(root, limit, list);
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && root.TryGetProperty("objects", out var objects)
                 && objects.ValueKind == JsonValueKind.Array)
        {
            CollectSearchItems(objects, limit, list);
        }

        return list;
    }

    /// <summary>把搜索结果数组逐项读成 PackageInfo（跳过缺名或形状不符的项，不因单项异常丢掉整页）。</summary>
    private void CollectSearchItems(JsonElement array, int limit, List<PackageInfo> into)
    {
        foreach (var item in array.EnumerateArray().Take(limit))
        {
            if (ReadSearchItem(item) is { } info) into.Add(info);
        }
    }

    // ---------- 版本列表 ----------

    /// <summary>
    /// 版本列表：`pnpm view &lt;name&gt; --json`（不指定字段时返回完整 packument）。
    ///
    /// 输出形态（与 npm view 同构）：
    /// {
    ///   "name": "react",
    ///   "dist-tags": { "latest": "18.3.1", "next": "19.0.0-rc.1" },
    ///   "versions": ["0.0.1", "0.0.2", ...],        ← 常规为版本号字符串数组
    ///   "version": "18.3.1",                        ← 根上的 version 只描述"查询到的那个版本"
    ///   "deprecated": "…",                          ← 同上，只对根版本有效
    ///   "time": { "18.3.1": "2024-04-25T…Z" }       ← 版本 → 发布时间
    /// }
    /// 四种形态都兜住：versions 可能是字符串数组、{版本: manifest} 映射、单个字符串，
    /// 也可能整个缺失（此时退回根上的 version）。dist-tags.latest 用来在界面上标出"最新"，
    /// time 用来填充发布时间；两者缺失都不影响版本列表本身。
    /// </summary>
    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        var output = await RunCliAsync(["view", name, "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success)
        {
            if (LooksLikeNotFound(output.StdErr, output.StdOut))
                throw new ProcessException($"找不到包 {name}", "package_not_found", output.StdErr, output.ExitCode);
            output.EnsureSuccess($"pnpm view {name}");
        }

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"pnpm view {name}")
                        ?? throw new ProcessException($"找不到包 {name}", "package_not_found", "", 0);

        var root = doc.RootElement;
        var versionNames = new List<string>();
        // 版本号 → 该版本的 deprecated 说明（只有对象形态的 versions 或根版本才带得到）
        var deprecatedNote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("versions", out var vers))
        {
            switch (vers.ValueKind)
            {
                case JsonValueKind.Object:
                    // 形态 A：{ "18.3.1": { "deprecated": "…", ... }, ... }
                    foreach (var p in vers.EnumerateObject())
                    {
                        versionNames.Add(p.Name);
                        if (p.Value.ValueKind == JsonValueKind.Object
                            && ReadString(p.Value, "deprecated") is { Length: > 0 } note)
                        {
                            deprecatedNote[p.Name] = note;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    // 形态 B：版本号字符串数组；少数 registry 会返回完整 manifest 数组，一并支持
                    foreach (var e in vers.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            if (e.GetString() is { Length: > 0 } s) versionNames.Add(s);
                        }
                        else if (e.ValueKind == JsonValueKind.Object
                                 && ReadString(e, "version") is { Length: > 0 } ov)
                        {
                            versionNames.Add(ov);
                            if (ReadString(e, "deprecated") is { Length: > 0 } dn) deprecatedNote[ov] = dn;
                        }
                    }
                    break;

                case JsonValueKind.String:
                    // 形态 C：单版本字符串
                    if (vers.GetString() is { Length: > 0 } one) versionNames.Add(one);
                    break;
            }
        }

        // 形态 D：没有 versions 列表时，用根上的 version 当作唯一已知版本。
        // 只在列表为空时使用，否则会与列表里的同名版本重复。
        if (versionNames.Count == 0 && root.ValueKind == JsonValueKind.Object
            && ReadString(root, "version") is { Length: > 0 } singleVersion)
        {
            versionNames.Add(singleVersion);
        }

        // 根上的 deprecated 描述的是"根 version 所指向的那个版本"。
        // 真机验证：`pnpm view left-pad --json` 给出 root version=1.3.0、root deprecated="use
        // String.prototype.padStart()"，而 versions 数组里同样有 1.3.0 —— 若只在"没有 versions
        // 列表时"才读它（早期实现），已废弃的最新版会被显示成正常版本，丢掉关键信息。
        // 因此这里无条件把这句说明挂到根版本上：deprecated 是用户决策的重要依据，不能漏。
        if (root.ValueKind == JsonValueKind.Object
            && ReadString(root, "version") is { Length: > 0 } rootVersion
            && ReadString(root, "deprecated") is { Length: > 0 } rootDeprecated)
        {
            deprecatedNote[rootVersion] = rootDeprecated;
        }

        if (versionNames.Count == 0)
            throw new ProcessException($"找不到包 {name}", "package_not_found", "", 0);

        // 真机验证：pnpm 会同时输出 "dist-tags" 与 "distTags" 两个别名键（内容相同），
        // 顺手兜住两种拼写，避免某个 pnpm 版本只给出其中一个时"最新"标记丢失。
        var latest = ReadString(root, "dist-tags", "latest") ?? ReadString(root, "distTags", "latest");

        return versionNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => new PackageVersion
            {
                Version = v,
                IsPrerelease = IsPrerelease(v),
                PublishedAt = ReadPublishedAt(root, v),
                Deprecated = deprecatedNote.ContainsKey(v),
                // Note 同时承载"是否被 dist-tags 指向为最新"与"废弃说明"两类信息
                //（两者可能同时成立，见 BuildNote 注释）
                Note = BuildNote(v, latest, deprecatedNote)
            })
            .OrderByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- 依赖 ----------

    /// <summary>
    /// 直接依赖：`pnpm view &lt;name&gt;@&lt;version&gt; --json`（不指定版本时取 latest）。
    ///
    /// 输出里三个字段各是一个 { 包名: 版本区间 } 对象，与 npm view 完全一致
    /// （真机验证 `pnpm view express@4.19.2 --json` → "dependencies": {"accepts":"~1.3.8", ...}）：
    ///   "dependencies":         { "loose-envify": "^1.1.0" }
    ///   "peerDependencies":     { "react": "&gt;=16.8" }
    ///   "optionalDependencies": { "fsevents": "^2.3.2" }
    /// 注意：registry 元数据里通常**没有** devDependencies（发布产物不带），所以 IsDev 恒为 false ——
    /// 这不是省略，而是该信息在 registry 里一般不成立，编不出来。
    /// 真机补充：镜像 registry（如 npmmirror）会把 devDependencies 也留在 packument 里，
    /// 但那是"开发此包时才需要"的依赖，安装该包**不会**装上它们，故仍刻意不收，
    /// 否则依赖树会凭空膨胀成几百个包，与真实安装结果不符。
    /// peer 依赖在 pnpm 下默认严格校验（strict-peer-dependencies），与 dependencies 同级列出。
    /// </summary>
    public override async Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(version) ? name : $"{name}@{version}";
        var output = await RunCliAsync(["view", target, "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"pnpm view {target}");
        if (doc is null) return [];

        var list = new List<DependencyRef>();
        void Collect(string property)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty(property, out var deps)) return;
            if (deps.ValueKind != JsonValueKind.Object) return;
            foreach (var p in deps.EnumerateObject())
                list.Add(new DependencyRef(p.Name, p.Value.GetString() ?? "*", IsDev: false));
        }

        Collect("dependencies");
        Collect("peerDependencies");
        Collect("optionalDependencies");
        return list;
    }

    // ---------- 安装步骤规划 ----------

    /// <summary>
    /// 按**下载方式**把 pnpm 安装拆成四个子项 —— 这就是进度界面逐行显示的数据来源。
    ///
    /// 步骤与权重为什么这么分：
    ///   1. DependencyResolve：解析依赖图。pnpm 会读 lockfile / registry 元数据把整棵依赖树
    ///      扁平化，并做 peer 依赖求解；纯元数据往返，权重小（0.15）。
    ///   2. RegistryMetadata：为树中每个包拉取 packument（tarball 地址、integrity 哈希）。
    ///      多数包命中本地元数据缓存，权重最小（0.10）。
    ///   3. RegistryTarball：真正的下载大头（0.60）。
    ///      **pnpm 使用内容寻址存储（content-addressable store，位于
    ///      &lt;PNPM_HOME&gt;/store 或 ~/.local/share/pnpm/store）**：tarball 按内容哈希落盘，
    ///      同一包在不同项目、不同版本间只解压一份；因此这一步只有在 store 未命中时才走网络，
    ///      命中时几乎是瞬时的 —— 界面会看到该子项很快跳到下一项，属正常现象。
    ///   4. LinkWrite：把 store 里的文件**硬链接**进项目的 node_modules/.pnpm，
    ///      再从顶层按符号链接暴露依赖（pnpm 的 node_modules 布局）。这一步不做网络 IO，
    ///      但会写大量链接项，故给 0.15。
    ///
    /// 只规划、不执行：真正的执行由安装器按这些步骤驱动并回填进度。
    /// </summary>
    public override Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        // 规划本身是纯内存操作，但契约要求传递取消令牌：已取消就不返回步骤
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<InstallStep>>(ct);

        var target = string.IsNullOrWhiteSpace(version) ? name : $"{name}@{version}";

        IReadOnlyList<InstallStep> steps =
        [
            new InstallStep
            {
                Title = $"解析 {target} 的依赖树", Method = DownloadMethod.DependencyResolve, Weight = 0.15
            },
            new InstallStep
            {
                Title = "读取注册表元数据（tarball 地址与完整性哈希）",
                Method = DownloadMethod.RegistryMetadata, Weight = 0.10
            },
            new InstallStep
            {
                Title = "下载压缩包进内容寻址存储",
                Method = DownloadMethod.RegistryTarball, Weight = 0.60
            },
            new InstallStep
            {
                Title = "硬链接入库并生成符号链接",
                Method = DownloadMethod.LinkWrite, Weight = 0.15
            },
        ];
        return Task.FromResult(steps);
    }

    // ---------- 解析辅助 ----------

    /// <summary>
    /// 把搜索结果里的一项读成 <see cref="PackageInfo"/>。
    /// 兼容扁平包对象与 {"package":{...},"score":{...}} 包装两种形状。
    /// 下载量字段刻意不填：pnpm 无官方下载量接口（见 Capabilities 注释），
    /// 若此处瞎填会让界面误以为拿到了官方数据，应由 GitHub Stars 兜底逻辑处理。
    /// </summary>
    private PackageInfo? ReadSearchItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var pkg = item;
        if (item.TryGetProperty("package", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            pkg = wrapped;

        var name = ReadString(pkg, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 主页优先取 links.homepage（npm registry 搜索结果的规范位置），退回顶层 homepage
        var homepage = ReadString(pkg, "links", "homepage") ?? ReadString(pkg, "homepage");

        return new PackageInfo
        {
            Source = Kind,
            Name = name,
            Summary = ReadString(pkg, "description"),
            LatestVersion = ReadString(pkg, "version"),
            License = ReadString(pkg, "license"),
            Homepage = string.IsNullOrEmpty(homepage) ? null : homepage
        };
    }

    /// <summary>读取对象上的字符串字段；类型不是字符串（如 license 的 {type,url} 形态）时返回 null。</summary>
    private static string? ReadString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>读取嵌套字符串字段，例如 dist-tags.latest。</summary>
    private static string? ReadString(JsonElement obj, string outer, string inner)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(outer, out var o)) return null;
        return ReadString(o, inner);
    }

    /// <summary>
    /// packument 的 time 映射（版本 → ISO 8601 发布时间）。
    /// 该字段并非所有 view 输出都带；缺失或格式不可解析时返回 null，不影响版本列表。
    /// </summary>
    private static DateTimeOffset? ReadPublishedAt(JsonElement root, string version)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("time", out var time)) return null;
        var raw = ReadString(time, version);
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 组装版本的备注文本。两类信息都可能同时成立 —— 真机验证：left-pad 的 1.3.0 既是
    /// dist-tags.latest，又带废弃说明 "use String.prototype.padStart()"。
    /// <see cref="PackageVersion"/> 只有一个自由文本 Note 字段，所以这里把两者拼在一起，
    /// 保证"最新"与"已废弃原因"都不丢失：
    ///   · 只是最新      → "latest"
    ///   · 只是废弃      → 废弃说明
    ///   · 既最新又废弃  → "latest · 废弃说明"
    /// Deprecated 布尔位独立设置，界面若只想显示"已废弃"徽标可直接用它。
    /// </summary>
    private static string? BuildNote(string version, string? latest, Dictionary<string, string> deprecatedNote)
    {
        var isLatest = string.Equals(version, latest, StringComparison.OrdinalIgnoreCase);
        deprecatedNote.TryGetValue(version, out var dep);
        var note = string.IsNullOrEmpty(dep) ? null : Truncate(dep, 200);

        if (isLatest) return note is null ? "latest" : $"latest · {note}";
        return note;
    }

    /// <summary>粗略判断是否预发布版本（有 "-" 后缀且含字母，如 19.0.0-rc.1 / 1.0.0-beta.2）</summary>
    private static bool IsPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return false;
        return version[(dash + 1)..].Any(char.IsLetter);
    }
}
