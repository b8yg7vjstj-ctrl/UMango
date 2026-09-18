using System.Text.Json;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// WinGet 适配器（仅 Windows）。
///
/// **本适配器的解析逻辑未在 Windows 真机验证**（开发机为 macOS，winget 只随
/// Windows 10 1809+ / Windows 11 的「应用安装程序」分发）。
/// 因此所有解析器都刻意写成**宽容多形态**：字段名逐个回退、JSON 顶层结构逐一兼容，
/// 遇到完全认不出的输出时抛带原始片段的 parse_error，方便用户回报真机格式后补齐。
///
/// 命令形态（依据 winget 官方 CLI 文档，未真机验证）：
///   · 版本探测  winget --version
///   · 搜索      winget search --query &lt;q&gt; --output json --disable-interactivity --accept-source-agreements
///   · 详情/版本 winget show --id &lt;id&gt; --exact --output json --accept-source-agreements
///
/// 为什么每条命令都带 --accept-source-agreements：
///   winget 首次访问某个源会弹出源协议（msstore 还额外有商店协议），
///   交互式提示在 GUI 里会**挂死进程直到超时**，所以必须显式接受。
///
/// 为什么每条命令都带 --disable-interactivity：
///   禁掉进度条转圈、代理提示、更新提示等交互输出；否则进度动画会混进 stdout，
///   把 JSON 输出污染得无法解析（与基类 ParseJson 的容错是双保险）。
///
/// winget **不暴露依赖关系**（winget 只会安装清单里声明的依赖，不对外提供查询），
/// 所以本适配器**不覆盖 GetDependenciesAsync**（保留基类的"返回空列表"），
/// 并把 Capabilities.ResolveDependencies 置为 false。
/// </summary>
public sealed class WingetPackageManager : CliPackageManagerBase
{
    public override PackageSourceKind Kind => PackageSourceKind.Winget;


    /// <summary>
    /// 列出已安装的包。
    ///
    /// ⚠️ **未在真机验证**（winget 仅存在于 Windows，本机是 macOS）。
    /// 命令：`winget list --output json --disable-interactivity --accept-source-agreements`。
    /// JSON 结构在不同 winget 版本间不一致，沿用与 search 相同的宽容解析
    /// （Sources[].Packages[] / Packages[] / 扁平数组三种形态都接受），
    /// 字段名同样做多次回退（PackageIdentifier|PackageId|Id、Version|PackageVersion）。
    /// </summary>
    public override async Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await RunCliAsync(
            ["list", "--output", "json", "--disable-interactivity", "--accept-source-agreements"],
            null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        using var doc = ParseJson<JsonDocument>(output.StdOut, "winget list");
        if (doc is null) return [];

        var packages = EnumerateSearchItems(doc.RootElement);
        var list = new List<InstalledPackage>();
        foreach (var item in packages)
        {
            var id = FirstString(item, "PackageIdentifier", "PackageId", "Id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new InstalledPackage
            {
                Source = Kind,
                Name = id,
                Version = FirstString(item, "Version", "PackageVersion"),
                InstallPath = InstallDirectory
            });
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 能力声明：
    ///   · OfficialDownloadCounts = false —— winget 没有任何公开的下载量接口
    ///     （search / show 的 JSON 里都没有该字段），界面不要显示下载量列；
    ///   · ResolveDependencies = false —— winget 不暴露依赖关系（见类注释）。
    /// </summary>
    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: false,
        OfficialDownloadCounts: false, Install: true, Uninstall: true, Upgrade: true);

    public override string InstallHint =>
        "winget 是 Windows 10 1809+ / Windows 11 自带的「应用安装程序」。" +
        "若缺失：在 Microsoft Store 搜索「应用安装程序 / App Installer」安装，" +
        "或从 https://github.com/microsoft/winget-cli/releases 下载 .msixbundle；" +
        "企业环境可试用 winget-cli 的 offline 分发。";

    /// <summary>
    /// 平台默认安装目录。
    ///
    /// **注意：这不是"安装目录"，而是 winget 的包缓存 / 清单根目录。**
    /// winget 与 npm / brew 不同，它**没有单一安装目录**：
    ///   · 便携（zip/portable）包解到 %LOCALAPPDATA%\Microsoft\WinGet\Packages\&lt;包ID&gt;；
    ///   · exe/msi/msix 安装器包则各自装到自己的位置（Program Files、AppData 等），
    ///     winget 只记录卸载信息，不集中存放文件。
    /// 界面把它作为子标题展示时，含义是"该管理器的包缓存根目录"。
    /// </summary>
    protected override string DefaultInstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

    /// <summary>
    /// 返回 null：winget **没有**形如 `npm root -g` / `brew --prefix` 的
    /// "查询安装根目录"命令，包位置只能从 `winget list` / `show` 的清单里逐包得知，
    /// 没有全局单一答案。因此直接使用平台默认值（基类会跳过探测命令）。
    /// </summary>
    protected override string[]? InstallDirArgs => null;

    /// <summary>
    /// 如实标注：本适配器的命令形态与 JSON 解析**未在 Windows 真机验证**
    /// （开发机是 macOS，无法运行 winget）。界面会把这句话显示给用户，
    /// 提示解析结果可能需要按真机输出修正。
    /// </summary>
    protected override string? VerificationNote =>
        "winget 适配器未在 Windows 真机验证（开发环境为 macOS，无法运行 winget）：" +
        "命令参数与 JSON 字段名依据官方文档编写，解析器已做多形态兼容；" +
        "若某包搜索/详情返回 parse_error，请把错误里的原始片段回报以修正字段映射。";

    // ------------------------------------------------------------------
    // 搜索
    // ------------------------------------------------------------------

    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // --query      关键字（不加 --query 时 winget 会把参数当子命令解析）
        // --count      限制返回条数（winget 的返回条数上限；解析时仍会再 Take 一次兜底）
        // --output json  机器可读输出，本适配器只认这种格式（表格形态易因列宽截断）
        // --disable-interactivity / --accept-source-agreements  见类注释（防挂死、防污染）
        var output = await RunCliAsync(
            [
                "search",
                "--query", query,
                "--count", Math.Max(1, limit).ToString(),
                "--output", "json",
                "--disable-interactivity",
                "--accept-source-agreements"
            ],
            null, SearchTimeoutMs, ct).ConfigureAwait(false);

        // winget 找不到包时 exit code 非 0 且输出里带 "No package found" 之类字样；
        // 基类 LooksLikeNotFound 会把 "not found" / "no package found" 归一化为 package_not_found。
        if (!output.Success && LooksLikeNotFound(output.StdErr, output.StdOut))
            throw new ProcessException(
                $"找不到与 {query} 匹配的包", "package_not_found", output.StdErr, output.ExitCode);
        output.EnsureSuccess($"winget search {query}");

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"winget search {query}")
                        ?? throw new ProcessException(
                            $"winget search {query} 未返回内容",
                            "parse_error", Truncate(output.StdOut, 200), output.ExitCode);

        var list = new List<PackageInfo>();
        foreach (var item in EnumerateSearchItems(doc.RootElement))
        {
            if (list.Count >= limit) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            // 包标识：不同 winget 版本字段名不同 → PackageIdentifier / PackageId / Id 依次尝试
            var id = FirstString(item, "PackageIdentifier", "PackageId", "Id");
            if (string.IsNullOrWhiteSpace(id)) continue;   // 没有标识的行对界面无用，跳过

            // 名称字段也多变 → PackageName / Name；winget 有时把区域后缀拼进名字（"msi (zh-CN)"），去掉
            var displayName = FirstString(item, "PackageName", "Name");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = id;

            // 简介：多数版本给 ShortDescription / Description；Id 列在 Name 缺失时也被当作名字用过
            var summary = FirstString(item, "ShortDescription", "Description", "Summary");
            var publisher = FirstString(item, "Publisher", "PublisherName", "Author");
            if (!string.IsNullOrWhiteSpace(publisher))
                summary = string.IsNullOrWhiteSpace(summary)
                    ? $"发布者：{publisher}"
                    : $"发布者：{publisher} · {summary}";

            // 版本：Version / PackageVersion 两个名字都出现过
            var version = FirstString(item, "Version", "PackageVersion");

            list.Add(new PackageInfo
            {
                Source = Kind,
                Name = StripLocaleSuffix(displayName!),
                Summary = summary,
                LatestVersion = string.IsNullOrWhiteSpace(version) ? null : version,
                // 注意：PackageInfo 没有独立字段承载发布者（Publisher / PublisherName / Author），
                // 而发布者信息对用户有价值 → 折进 Summary 前缀，避免丢失。
                // License / Homepage 两个字段名也都存在多种写法，逐个回退。
                License = FirstString(item, "License", "LicenseUrl"),
                Homepage = FirstString(item, "Homepage", "PackageUrl", "HomepageUrl"),
                // 下载量：winget 无公开计数接口 → 保持 null，且不设 DownloadCountSource，
                // 界面据此显示"无数据"而不是"0 次下载"（Capabilities.OfficialDownloadCounts = false）
            });
        }
        return list;
    }

    // ------------------------------------------------------------------
    // 版本列表
    // ------------------------------------------------------------------

    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // --id <name>  按包标识精确查询（winget show 也接受 --name，但 --id 更明确）
        // --exact      不做模糊匹配，避免 "Git" 命中一堆同名包
        // --output json  只认 JSON：表格形态在中文 Windows 下会被列宽截断
        // 这里不再追加 --versions：带 --versions 时部分版本输出**不含** JSON 结构，
        // 统一用 show 的 JSON 并从中读取 Versions 数组，兼容性更好。
        var output = await RunCliAsync(
            [
                "show", "--id", name, "--exact",
                "--output", "json",
                "--disable-interactivity",
                "--accept-source-agreements"
            ],
            null, SearchTimeoutMs, ct).ConfigureAwait(false);

        if (!output.Success && LooksLikeNotFound(output.StdErr, output.StdOut))
            throw new ProcessException(
                $"找不到包 {name}", "package_not_found", output.StdErr, output.ExitCode);
        output.EnsureSuccess($"winget show --id {name}");

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"winget show --id {name}")
                        ?? throw new ProcessException(
                            $"winget show --id {name} 未返回内容",
                            "parse_error", Truncate(output.StdOut, 200), output.ExitCode);

        // show 的 JSON 顶层通常是**单个对象**，但部分版本/多个匹配结果时也可能给数组
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object) { root = el; break; }
            }
        }

        var versions = new List<PackageVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 形态一：详细版本数组 "Versions": [ { "PackageVersion": "1.2.3" }, ... ]
        //         （嵌套对象里也可能只有 "Version"/"PackageId.Version" 这样的键）
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Versions", out var vers)
            && vers.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vers.EnumerateArray())
            {
                var text = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Object => FirstString(v, "PackageVersion", "Version", "Identifier"),
                    _ => null
                };
                AddVersion(versions, seen, text);
            }
        }

        // 形态二：只有单个 "PackageVersion" / "Version" —— 此时列表里只有当前最新版
        if (versions.Count == 0)
            AddVersion(versions, seen, FirstString(root, "PackageVersion", "Version"));

        // 两种形态都取不到 → 抛 parse_error 并附原始片段（按需求，绝不静默返回空列表）
        if (versions.Count == 0)
            throw new ProcessException(
                $"无法从 winget show --id {name} 的输出中解析出版本号（顶层结构 {root.ValueKind}）：" +
                "预期 \"Versions\": [ { \"PackageVersion\": ... } ] 或单个 \"Version\"/\"PackageVersion\" 字段",
                "parse_error", Truncate(output.StdOut, 200), output.ExitCode);

        // winget 的版本列表大致按新→旧给出，但不同源不完全保证；这里按字符串倒序排一遍
        return versions
            .OrderByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------
    // 安装步骤规划（进度界面按下载方式逐项显示的数据来源）
    // ------------------------------------------------------------------

    /// <summary>
    /// winget 的一次安装按**下载方式**拆成三步，每步在进度界面各占一行子项：
    ///   1. 拉取清单（ManifestFetch）—— 从源（winget-pkgs / msstore）取包清单，
    ///      清单里才有 InstallerUrl、Sha256、InstallerType、Scope 等真实下载信息；
    ///   2. 直链下载安装器（DirectUrl）—— winget 直接 HTTP 下载清单里声明的
    ///      .exe / .msi / .msix / .zip 安装器（这是耗时最大的一步，权重最高）；
    ///   3. 链接与写入（LinkWrite）—— 执行安装器落盘，并为 portable 包创建
    ///      Links / shim 与注册卸载信息。
    ///
    /// 权重之和为 1.0，供父进度条加权汇总。
    /// </summary>
    public override Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 目标描述同时用于步骤标题：指定版本时写成 "名字 版本"，便于界面区分多次安装
        var target = string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";

        IReadOnlyList<InstallStep> steps =
        [
            new InstallStep
            {
                Title = $"拉取 {target} 的清单",
                Method = DownloadMethod.ManifestFetch,
                Weight = 0.15
            },
            new InstallStep
            {
                Title = $"下载 {target} 的安装器",
                Method = DownloadMethod.DirectUrl,
                Weight = 0.70
            },
            new InstallStep
            {
                Title = $"执行安装并写入链接 {target}",
                Method = DownloadMethod.LinkWrite,
                Weight = 0.15
            },
        ];
        return Task.FromResult(steps);
    }

    // ------------------------------------------------------------------
    // 解析辅助（宽容多形态：winget 的 JSON 结构与字段名跨版本不一致）
    // ------------------------------------------------------------------

    /// <summary>
    /// 搜索超时。winget 首次搜索可能要更新源索引（msstore 源尤其慢），
    /// 比基类默认 120s 再放宽一点，避免首次使用就超时。
    /// </summary>
    private const int SearchTimeoutMs = 180_000;

    private static void AddVersion(
        List<PackageVersion> sink, HashSet<string> seen, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        var v = version.Trim();
        if (!seen.Add(v)) return;   // 去重：winget 偶尔会把同一版本列出多次
        sink.Add(new PackageVersion { Version = v, IsPrerelease = IsPrerelease(v) });
    }

    /// <summary>
    /// 遍历搜索结果的包条目，**同时兼容三种顶层结构**：
    ///   1. { "Sources": [ { "Packages": [ ... ] } ] }  ← 现代 winget（多源分组）
    ///   2. { "Packages": [ ... ] }                     ← 单源扁平对象
    ///   3. [ { ... }, ... ]                            ← 极扁平数组（部分版本 --output json）
    /// 另外兜一层：Sources 里若没有 Packages，就把 Sources 自身当数组遍历。
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateSearchItems(JsonElement root)
    {
        // 形态 3：顶层就是数组
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();

        if (root.ValueKind != JsonValueKind.Object) return [];

        // 形态 1：{ "Sources": [ { "Packages": [...] } ] }
        if (root.TryGetProperty("Sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array)
        {
            var collected = new List<JsonElement>();
            foreach (var src in sources.EnumerateArray())
            {
                if (src.ValueKind != JsonValueKind.Object) continue;

                if (src.TryGetProperty("Packages", out var pkgs)
                    && pkgs.ValueKind == JsonValueKind.Array)
                {
                    collected.AddRange(pkgs.EnumerateArray());
                }
                else if (src.TryGetProperty("Package", out var single)
                         && single.ValueKind == JsonValueKind.Object)
                {
                    // 某些版本单个结果时给 "Package": {...} 而不是数组
                    collected.Add(single);
                }
            }
            if (collected.Count > 0) return collected;
            // Sources 存在但一条都没解出来 → 继续往下试形态 2，不直接放弃
        }

        // 形态 2：{ "Packages": [ ... ] }
        if (root.TryGetProperty("Packages", out var direct)
            && direct.ValueKind == JsonValueKind.Array)
        {
            return direct.EnumerateArray().ToList();
        }

        return [];
    }

    /// <summary>
    /// 按候选名依次取第一个非空字符串属性（字段名回退顺序由调用处给出）。
    /// 同时容忍两种非常见情况：
    ///   · 值是数组/对象（个别版本把 name 包成数组或本地化对象）→ 取里面第一个字符串；
    ///   · 值不是字符串（数字等）→ 用 GetRawText 兜底。
    /// </summary>
    private static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var text = ReadAsString(value);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    /// <summary>把一个 JSON 值读成字符串（宽容处理字符串/数组/对象/数字）。</summary>
    private static string? ReadAsString(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.GetRawText();

            // 有的源把名称写成 { "zh-CN": "..." } 或 [ {"...": "..."} ]，取第一个可用字符串
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject())
                {
                    var inner = ReadAsString(p.Value);
                    if (!string.IsNullOrWhiteSpace(inner)) return inner;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var el in value.EnumerateArray())
                {
                    var inner = ReadAsString(el);
                    if (!string.IsNullOrWhiteSpace(inner)) return inner;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 去掉 winget 名称里拼上的本地化后缀，例如 "msi (zh-CN)" → "msi"。
    /// winget 在多语言源下会把区域码附在名称尾部，直接显示会比较噪。
    /// 只在后缀形如 "(xx-XX)" 时才剥离，普通括号（如 "Tool (beta)"）不动。
    /// </summary>
    private static string StripLocaleSuffix(string name)
    {
        var text = name.Trim();
        if (!text.EndsWith(')')) return text;

        var open = text.LastIndexOf('(');
        if (open <= 0) return text;

        var inner = text[(open + 1)..^1].Trim();
        // 形如 zh-CN / en-US / zh-Hans：2~3 位语言码 + '-' + 2~4 位地区码
        var parts = inner.Split('-');
        if (parts.Length != 2) return text;
        if (parts[0].Length is < 2 or > 3 || parts[1].Length is < 2 or > 4) return text;
        if (!parts[0].All(char.IsLetter) || !parts[1].All(char.IsLetterOrDigit)) return text;

        return text[..open].Trim();
    }

    /// <summary>粗略判断是否预发布版本（含 - 且带字母标记，如 1.2.3-beta）。</summary>
    private static bool IsPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return false;
        var pre = version[(dash + 1)..].ToLowerInvariant();
        return pre.Any(char.IsLetter);
    }
}
