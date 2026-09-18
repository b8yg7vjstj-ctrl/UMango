using System.Globalization;
using System.Text;
using System.Text.Json;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// git 适配器：把「包源」映射到 Git 仓库上。
///
/// 与其他适配器的关键差异：
///   1. **git 没有注册表**。不存在全局唯一的"包名"，包就是「某个远端仓库」，
///      因此 Name 一律用 <c>owner/repo</c>（或完整 URL）表达；
///   2. **版本列表 = 远端 tag**。用本机 `git ls-remote --tags --refs` 读取，
///      对 GitHub / GitLab / 自建 Git 主机都通用，不绑定任何一家厂商的 API；
///   3. **搜索只能走 GitHub 搜索 API**（git 自身没有搜索能力），
///      用系统 curl 调用 api.github.com —— 见 <see cref="SearchAsync"/> 与
///      <see cref="GetTextViaCurlAsync"/> 的注释：不引入 HTTP 依赖库；
///   4. **依赖解析不适用**：仓库没有统一的依赖元数据，能力位如实声明为 false，
///      界面据此不展示依赖树，避免给出误导性结果；
///   5. **「下载量」对仓库不成立**，改用 GitHub Star 数，并如实标记成兜底指标
///      （DownloadCountIsFallback = true + DownloadCountSource = "github stars"），
///      界面必须能把它与官方下载量区分开 —— 这是修过的真实缺陷，不要退化。
///
/// 共性能力（平台门控、CLI 定位、版本探测、超时/取消、容错 JSON、package_not_found 归一化）
/// 全部由 <see cref="CliPackageManagerBase"/> 提供，此处不重复实现。
/// </summary>
public sealed class GitPackageManager : CliPackageManagerBase
{

    /// <summary>
    /// 列出本地克隆的仓库。
    ///
    /// **概念说明**：git 没有"安装"这个概念 —— 它只是把仓库克隆到某个目录。
    /// 因此这里不是"列出已安装的包"，而是**扫描默认克隆目录，找出其中的 git 仓库**。
    /// 这一语义差异在界面文案上要体现为"本地仓库"而不是"已安装"。
    ///
    /// 判定方式：目录下存在 .git（目录或文件，后者用于 worktree / submodule）。
    /// 深度限制为 2，避免在庞大的目录树上做全盘扫描。
    /// </summary>
    public override Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var root = DefaultInstallDirectory;
        if (!Directory.Exists(root)) return Task.FromResult<IReadOnlyList<InstalledPackage>>([]);

        var repos = ScanLocalRepositories(root);
        var list = new List<InstalledPackage>();
        try
        {
            foreach (var dir in repos)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(new InstalledPackage
                {
                    Source = Kind,
                    Name = Path.GetFileName(dir),
                    Version = null,          // 本地仓库的"版本"取决于当前检出，不在这里猜
                    InstallPath = dir
                });
            }
        }
        catch (Exception)
        {
            // 扫描本地目录失败不应抛到界面：返回已找到的部分
        }
        return Task.FromResult<IReadOnlyList<InstalledPackage>>(
            list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// 扫描目录，找出其中的 git 仓库路径。
    /// 抽成 public static 是为了**可测试** —— 否则测试要么造一个真实仓库树放在默认目录里
    /// （会污染用户环境），要么继承本类（本类是 sealed）。
    /// </summary>
    public static IReadOnlyList<string> ScanLocalRepositories(string root, int maxDepth = 2)
    {
        if (!Directory.Exists(root)) return [];
        return EnumerateRepos(root, depth: 0, maxDepth: maxDepth).ToList();
    }

    private static IEnumerable<string> EnumerateRepos(string dir, int depth, int maxDepth)
    {
        if (depth > maxDepth) yield break;

        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
        {
            yield return dir;
            yield break;   // 找到仓库就不再往里钻（避免列出子模块）
        }

        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); }
        catch (Exception) { yield break; }   // 无权限等：跳过

        foreach (var sub in subs)
        {
            foreach (var found in EnumerateRepos(sub, depth + 1, maxDepth))
                yield return found;
        }
    }

    /// <summary>
    /// `git ls-remote` 的超时。
    ///
    /// 必须显式限时：GitHub 不可达时 git 会**一直挂着不返回**
    /// （真机实测：直连 github.com 的 ls-remote 直接挂死，直到外部 timeout 杀掉）。
    ///
    /// 为什么取 45 秒而不是更激进的 25 秒：真机实测走 GitHub 镜像时一次
    /// ls-remote 需要 **12 ~ 28 秒**（仓库引用越多越慢），25 秒会把正常请求误判成超时。
    /// 直连被墙时仍会在 45 秒内结束并报 timeout，不会无限等待。
    /// </summary>
    private const int RemoteTimeoutMs = 45_000;

    /// <summary>curl 单次请求超时（秒）</summary>
    private const int CurlTimeoutSeconds = 20;

    /// <summary>GitHub 搜索结果单页上限（API 上限 100，取 50 兼顾响应速度）</summary>
    private const int MaxSearchLimit = 50;

    /// <summary>
    /// 用 `curl -w` 把 HTTP 状态码带出 stdout 时的分隔标记。
    ///
    /// 为什么必须取状态码：curl 遇到 4xx/5xx **仍然以退出码 0 返回**，
    /// 只把错误体（<c>{"message":"API rate limit exceeded..."}</c>）写进 stdout。
    /// 不校验状态码就会把限流错误体当成搜索结果解析 —— 这是踩过的坑。
    /// </summary>
    private const string StatusMarker = "\nUMANGO_HTTP_STATUS:";

    private const string UserAgent = "UMango/0.1";

    /// <summary>可选：GitHub 代理/镜像前缀（例如 <c>http://gh-proxy.org/</c>）</summary>
    private const string ProxyEnvVar = "UMANGO_GITHUB_PROXY";

    /// <summary>可选：GitHub Token，可把未认证限流从 60 次/小时提升到 5000 次/小时</summary>
    private const string TokenEnvVar = "UMANGO_GITHUB_TOKEN";

    public override PackageSourceKind Kind => PackageSourceKind.Git;

    /// <summary>
    /// 能力声明。**四个 false 都是"业务上不成立"，不是"还没做"**：
    ///   · ResolveDependencies   —— 仓库没有统一的依赖元数据
    ///     （package.json / *.csproj 只是某类项目的约定，不是仓库的属性）；
    ///   · OfficialDownloadCounts —— git 主机不提供下载量计数，仓库场景的原生指标是 Star 数；
    ///   · Uninstall             —— 克隆结果就是普通目录，没有可查的"安装记录"；
    ///   · 其余为 true：Search（GitHub API）、ListVersions（ls-remote 读 tag）、
    ///     Install / Upgrade（都是"克隆 + 检出"）。
    /// </summary>
    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: false,
        OfficialDownloadCounts: false, Install: true, Uninstall: false, Upgrade: true);

    public override string InstallHint =>
        "安装 Git：https://git-scm.com/downloads（macOS 可执行 `xcode-select --install`，" +
        "Windows 可执行 `winget install Git.Git`）";

    /// <summary>
    /// git 没有"全局安装目录"这个概念（git 本身是 CLI，克隆落点由用户决定）。
    /// 这里的值是**默认克隆目录**：既作为界面子标题显示，也是克隆时的默认落点。
    /// </summary>
    protected override string DefaultInstallDirectory => SystemInfo.Platform switch
    {
        // Windows 上开发者（VS / dotnet）的惯例目录：%USERPROFILE%\source\repos
        PlatformKind.Windows => Path.Combine(SystemInfo.HomeDirectory, "source", "repos"),
        // macOS / Linux 惯例：~/repos
        _ => Path.Combine(SystemInfo.HomeDirectory, "repos")
    };

    /// <summary>
    /// 显式返回 null：git **没有** "全局安装目录"，也就没有任何 CLI 参数能问出真实路径
    /// （不像 npm 有 `root -g`、brew 有 `--prefix`）。
    /// 基类看到 null 会跳过安装目录解析，直接把 <see cref="DefaultInstallDirectory"/> 当子标题。
    /// </summary>
    protected override string[]? InstallDirArgs => null;

    /// <summary>
    /// 验证情况说明（如实标注）。
    ///
    /// 已真机验证：tag 解析（对真实远端 jq 仓库跑通 `git ls-remote --tags --refs`，
    /// 19 个真实 tag 的排序正确）与 GitHub 搜索（真实 API 响应，字段映射与 Star 兜底标注均正确）。
    /// 未验证：在完整应用内的端到端流程；且两条链路都依赖网络 ——
    /// 本机直连 github.com 会挂死（报 timeout），需设置 UMANGO_GITHUB_PROXY 走镜像。
    /// </summary>
    protected override string? VerificationNote =>
        "tag 解析与 GitHub 搜索已对真实远端（jq 仓库）与真实 API 响应验证通过；" +
        "但其在完整应用内的端到端流程未验证，且两条链路都依赖网络：" +
        "直连 github.com 不可达时会报 timeout / network_error" +
        "（可设置 UMANGO_GITHUB_PROXY 指向 GitHub 镜像）。";

    // ------------------------------------------------------------------
    // 搜索（git 自己搜不了 → 走 GitHub 搜索 API，用系统 curl）
    // ------------------------------------------------------------------

    /// <summary>
    /// 用 GitHub 的 <c>search/repositories</c> 接口搜索仓库。
    ///
    /// 请求：<c>GET https://api.github.com/search/repositories?q=&lt;query&gt;&amp;sort=stars&amp;order=desc&amp;per_page=N</c>
    /// 响应：<c>{"total_count":N,"incomplete_results":false,"items":[{full_name, description,
    /// stargazers_count, license:{spdx_id}, homepage, ...}]}</c>
    ///
    /// 注意：搜索**不依赖 git CLI**（纯 curl），但界面只在 git 可用时才展示 git 源。
    /// </summary>
    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        // 空关键词不发请求：GitHub 会回 422，本地直接给出"没有结果"更省事也更省配额
        if (string.IsNullOrWhiteSpace(query)) return [];

        ct.ThrowIfCancellationRequested();

        var perPage = Math.Clamp(limit, 1, MaxSearchLimit);
        var url = "https://api.github.com/search/repositories"
                + $"?q={UrlEncode(query.Trim())}&sort=stars&order=desc&per_page={perPage}";

        var body = await GetTextViaCurlAsync(url, ct).ConfigureAwait(false);

        using var doc = ParseJson<JsonDocument>(body, "GitHub 仓库搜索")
                        ?? throw new ProcessException(
                            "GitHub 仓库搜索未返回内容", "parse_error", Truncate(body, 200), 0);

        // 正常响应才带 items[]；限流/参数错误时只有 message + documentation_url
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            var message = doc.RootElement.ValueKind == JsonValueKind.Object
                          && doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString()
                : null;
            throw new ProcessException(
                $"GitHub 搜索未返回 items 数组：{message ?? "响应格式异常"}",
                "network_error", Truncate(body, 200), -1);
        }

        var list = new List<PackageInfo>();
        foreach (var item in items.EnumerateArray().Take(perPage))
        {
            ct.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) continue;

            var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
            if (string.IsNullOrWhiteSpace(fullName)) continue;

            // Star 数：**仓库场景的原生指标**，不是下载量。
            // 必须同时给出 DownloadCountSource 与 DownloadCountIsFallback，
            // 否则界面会把 Star 当成官方下载量显示（这是修过的真实缺陷）。
            // 取不到时 DownloadCount 保持 null（界面显示"暂无数据"），绝不退化成 0。
            long? stars = item.TryGetProperty("stargazers_count", out var sc)
                          && sc.ValueKind == JsonValueKind.Number
                          && sc.TryGetInt64(out var n)
                ? n
                : null;

            list.Add(new PackageInfo
            {
                Source = Kind,
                Name = fullName!,                     // owner/repo，可直接回喂 GetVersionsAsync
                Summary = item.TryGetProperty("description", out var d) ? d.GetString() : null,
                // 搜索结果不含 tag 信息：要拿"最新版本"就得对每个仓库各跑一次 ls-remote，
                // 一页几十个仓库就是几十次网络往返，这里不预取（详情页再取）。
                LatestVersion = null,
                Homepage = ReadHomepage(item, fullName!),
                License = ReadLicense(item),
                DownloadCount = stars,
                DownloadCountSource = "github stars",
                DownloadCountIsFallback = true,
            });
        }
        return list;
    }

    /// <summary>仓库主页：GitHub 没给 homepage 字段时退回仓库地址，保证卡片链接始终可用</summary>
    private static string? ReadHomepage(JsonElement item, string fullName)
    {
        var home = item.TryGetProperty("homepage", out var h) ? h.GetString() : null;
        if (!string.IsNullOrWhiteSpace(home)) return home.Trim();
        return $"https://github.com/{fullName}";
    }

    /// <summary>许可证：取 <c>license.spdx_id</c></summary>
    private static string? ReadLicense(JsonElement item)
    {
        if (!item.TryGetProperty("license", out var lic) || lic.ValueKind != JsonValueKind.Object)
            return null;

        var spdx = lic.TryGetProperty("spdx_id", out var s) ? s.GetString() : null;
        // GitHub 对无法识别的许可证返回 "NOASSERTION"（不是有效 SPDX 标识），
        // 直接显示会误导用户，这里按"未知"处理
        if (string.IsNullOrWhiteSpace(spdx) || spdx == "NOASSERTION") return null;
        return spdx;
    }

    /// <summary>
    /// 用**系统 curl** 取一段文本。
    ///
    /// 为什么调 curl 而不引 HTTP 库：
    ///   · 不新增 NuGet 依赖，Windows 10+ / macOS / Linux 都自带 curl；
    ///   · 与项目"外部命令统一走 ProcessRunner"的架构一致 —— 超时、取消、逐行回传复用同一套；
    ///   · git 场景下唯一的 HTTP 需求就是 GitHub 搜索，不值得为它拉一整棵 TLS 依赖树。
    ///
    /// 关键参数：
    ///   <c>-sS</c> 静默但保留错误信息；<c>-L</c> **必须带**（api.github.com 会 301 重定向，
    ///   不跟随时拿到的是 <c>{"message":"Moved Permanently"}</c> 而不是数据）；
    ///   <c>-w</c> 把 HTTP 状态码写到 stdout 末尾，由本方法切出来校验。
    /// </summary>
    private static async Task<string> GetTextViaCurlAsync(string url, CancellationToken ct)
    {
        var curl = ProcessRunner.Which("curl");
        if (curl is null)
            throw new ProcessException(
                "找不到 curl：UMango 用系统 curl 访问 GitHub API（Windows 10+ / macOS / Linux 均自带）",
                "cli_missing", "", -1);

        var target = ApplyProxy(url);

        var args = new List<string>
        {
            "-sS",
            "-L",
            "--max-time", CurlTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "-H", "Accept: application/json",
            "-H", $"User-Agent: {UserAgent}",
        };

        // 可选：带 Token 时未认证限流从 60 次/小时提升到 5000 次/小时
        var token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (!string.IsNullOrWhiteSpace(token))
        {
            args.Add("-H");
            args.Add($"Authorization: Bearer {token.Trim()}");
        }

        args.Add("-w");
        args.Add(StatusMarker + "%{http_code}");
        args.Add(target);

        var output = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Program = curl,
                Args = args,
                TimeoutMs = (CurlTimeoutSeconds + 10) * 1000
            }, null, ct).ConfigureAwait(false);

        if (output.TimedOut)
            throw new ProcessException(
                $"请求 {url} 超时（>{CurlTimeoutSeconds}s）", "timeout", output.StdErr, -1);

        // curl 自己失败（DNS 失败 / 代理不可达 / TLS 失败）：stderr 里是原因
        if (!output.Success)
            throw new ProcessException(
                $"curl 调用失败（退出码 {output.ExitCode}）：{Truncate(output.StdErr.Trim(), 200)}",
                "network_error", output.StdErr, output.ExitCode);

        // -w 的输出一定在正文之后，反向查找可避免正文里恰好含标记时切错位置
        var raw = output.StdOut;
        var at = raw.LastIndexOf(StatusMarker, StringComparison.Ordinal);
        var body = at >= 0 ? raw[..at] : raw;

        var status = 0;
        if (at >= 0)
            int.TryParse(raw[(at + StatusMarker.Length)..].Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out status);

        if (status is >= 200 and <= 299) return body;

        throw new ProcessException(
            DescribeHttpFailure(status, url), "network_error", Truncate(body.Trim(), 200), status);
    }

    /// <summary>把 HTTP 状态码翻译成用户看得懂、且能据此行动的原因</summary>
    private static string DescribeHttpFailure(int status, string url) => status switch
    {
        0 => $"请求 {url} 失败：curl 未返回 HTTP 状态码（网络中断或代理异常）",
        403 => "GitHub API 拒绝访问（403）：多为未认证限流（60 次/小时）；"
             + "可设置环境变量 UMANGO_GITHUB_TOKEN 提升到 5000 次/小时",
        404 => $"GitHub 返回 404：{url} 不存在",
        429 => "GitHub API 请求过于频繁（429），请稍后重试",
        _ => $"GitHub API 返回 HTTP {status}（{url}）"
    };

    // ------------------------------------------------------------------
    // 版本列表（远端 tag）
    // ------------------------------------------------------------------

    /// <summary>
    /// 读取远端全部 tag 作为版本列表：<c>git ls-remote --tags --refs &lt;url&gt;</c>
    ///
    /// 输出每行形如 <c>&lt;sha&gt;\trefs/tags/v1.2.3</c>（真机取样）。
    /// 用 ls-remote 而不是厂商 API 的原因：任何 Git 主机都能用，且不需要完整克隆。
    /// </summary>
    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            throw new ProcessException(
                "仓库名不能为空（可写 owner/repo 或完整 URL）", "package_not_found", "", -1);

        var url = NormalizeUrl(name);

        // 必须限时（见 RemoteTimeoutMs）：GitHub 不可达时 git 会一直挂着，不能让它无限等
        var output = await RunCliAsync(
            ["ls-remote", "--tags", "--refs", ApplyProxy(url)], null, RemoteTimeoutMs, ct)
            .ConfigureAwait(false);

        if (output.TimedOut)
            throw new ProcessException(
                $"读取 {url} 的 tag 超时（{RemoteTimeoutMs}ms）", "timeout", output.StdErr, -1);

        if (!output.Success)
            throw ClassifyLsRemoteFailure(output, url);

        var versions = ParseTags(output.StdOut);

        if (versions.Count == 0)
            // 仓库存在但没有任何 tag：这不是"找不到仓库"，也不该给一个空列表了事 ——
            // 界面要能区分"没有 tag（可能只用分支发布）"与"失败"
            throw new ProcessException(
                $"仓库 {url} 没有任何 tag（可能只用分支发布）", "internal_error", output.StdErr, 0);

        return versions;
    }

    /// <summary>
    /// 区分 `ls-remote` 的失败原因 —— 三种成因给用户的处置办法完全不同，
    /// 不能都笼统报成 command_failed。
    /// </summary>
    private static ProcessException ClassifyLsRemoteFailure(ProcessOutput output, string url)
    {
        var stderr = output.StdErr ?? "";
        var lower = stderr.ToLowerInvariant();

        // 1) 网络层失败：DNS 解析不了 / 连不上 / 超时 / 代理异常
        if (lower.Contains("could not resolve host")
            || lower.Contains("connection")
            || lower.Contains("timed out")
            || lower.Contains("unable to access")
            || lower.Contains("failed to connect")
            || lower.Contains("operation was aborted"))
        {
            return new ProcessException(
                $"无法访问 {url}：{Truncate(stderr.Trim(), 200)}",
                "network_error", stderr, output.ExitCode);
        }

        // 2) 仓库不存在 / 需要认证。
        //    真机实测（走 GitHub 镜像访问不存在的仓库，退出码 128）：
        //      fatal: could not read Username for 'https://gh-proxy.org': Device not configured
        //    即 git 在收到 404/401 后会去交互式要凭据。对用户来说这两种情况的处置相同
        //    （确认名字写对，或配置凭据访问私有仓库），因此统一归一化为 package_not_found。
        if (lower.Contains("could not read username")
            || lower.Contains("authentication failed")
            || lower.Contains("terminal prompts disabled")
            || lower.Contains("repository not found")
            || LooksLikeNotFound(stderr, output.StdOut))
        {
            return new ProcessException(
                $"找不到仓库 {url}（若为私有仓库，请先配置 Git 凭据）",
                "package_not_found", stderr, output.ExitCode);
        }

        // 3) 其余（远端拒绝、钩子报错、协议错误等）：保留原始 stderr 供用户判断
        return new ProcessException(
            $"git ls-remote {url} 失败（退出码 {output.ExitCode}）",
            "command_failed", stderr, output.ExitCode);
    }

    /// <summary>
    /// 解析 `git ls-remote --tags --refs` 的输出。
    ///
    /// 输出形态（真机取样）：
    /// <code>
    /// 46d1ce2667253f1a34cd389b6d00c0288ab0276f	refs/tags/1.6rc2
    /// bf61e95c0bce31e5a6080b92d4d9961c8055d83	refs/tags/jq-1.1
    /// </code>
    /// 因此按 \t 切分取后半段、剥掉 refs/tags/ 前缀即可；
    /// 没有制表符的行（空行、警告）与 <c>^{}</c> 解引用行一律跳过。
    ///
    /// `--refs` 本身已过滤解引用行，这里的判断是兜底：
    /// 旧版 git 或某些自建主机（Gitea/Gitolite）的输出不保证一致。
    ///
    /// 可见性为 public（而不是 private）：本工程没有配置 InternalsVisibleTo，
    /// 测试工程（UMango.Core.Tests）看不到 internal 成员。这里是纯函数、无副作用，公开出来便于直接单测。
    /// </summary>
    public static List<PackageVersion> ParseTags(string stdout)
    {
        var versions = new List<PackageVersion>();

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // 每行 "<sha>\t<ref>"：只认制表符分隔，避免把警告文本误当引用
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;

            var reference = line[(tab + 1)..].Trim();
            if (!reference.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;

            var tag = reference["refs/tags/".Length..].Trim();
            if (tag.Length == 0 || tag.EndsWith("^{}", StringComparison.Ordinal)) continue;

            versions.Add(new PackageVersion
            {
                Version = tag,                    // 保留原样（v1.2.3 / 1.2.3-rc.1 / nightly）
                IsPrerelease = IsPrereleaseTag(tag),
                Note = $"git tag `{tag}`"
            });
        }

        // 按版本号从新到旧。tag 常常不是合法 semver（v1.2 / release-2024-05 / 20240102），
        // 所以用宽松比较器：能切分就能比较，绝不因为格式怪就抛错。
        versions.Sort((a, b) => CompareVersions(b.Version, a.Version));

        // 同一 tag 名可能因换行/重复引用出现多行，排完序后相邻，去重保留其一
        var unique = new List<PackageVersion>(versions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in versions)
        {
            if (seen.Add(v.Version)) unique.Add(v);
        }
        return unique;
    }

    /// <summary>
    /// 预发布判定：预发布段里带字母（1.0.0-rc.1）或主段里带文本（nightly / jq-1.7rc2）才算非稳定版。
    ///
    /// 为什么预发布段是**纯数字**时不算预发布：与同项目的 npm 适配器同一套约定
    /// （"含 - 且带字母"）。严格 semver 会把 <c>1.0.0-1</c> 判为预发布，但 git 的 tag 里
    /// 纯数字后缀多半是日期（release-2024-05 / 20240102-1），把它们标成"预发布"会误导用户。
    /// 排序不受影响：预发布段始终参与比较，只是标签不同。
    /// </summary>
    private static bool IsPrereleaseTag(string tag)
    {
        var (main, pre) = SplitVersion(tag);

        foreach (var part in pre)
        {
            if (!part.IsNumber) return true;
        }
        // 主版本段里出现文本，说明它不是真正的版本号，而是浮动 tag（nightly / latest）
        foreach (var part in main)
        {
            if (!part.IsNumber) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 安装规划（克隆 + 检出）
    // ------------------------------------------------------------------

    /// <summary>
    /// git 的"安装" = 克隆仓库 + 检出目标版本，因此**必然是两个不同下载方式的子项**：
    /// 克隆把仓库对象（含全部历史与 tag）一次拉下来，是真正的网络开销；
    /// 检出只在本地切换引用（首次克隆后 HEAD 已指向默认分支），几乎不耗时 —— 权重 0.8 / 0.2。
    /// </summary>
    public override Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        // 规划本身是纯内存操作，但契约要求传递取消令牌：已取消就不返回步骤
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<InstallStep>>(ct);

        var url = NormalizeUrl(name);
        var checkout = string.IsNullOrWhiteSpace(version) ? "默认分支（HEAD）" : version!;

        IReadOnlyList<InstallStep> steps =
        [
            new InstallStep
            {
                Title = $"克隆仓库 {url}",
                Method = DownloadMethod.GitClone,
                Weight = 0.8
            },
            new InstallStep
            {
                Title = $"检出 {checkout}",
                Method = DownloadMethod.GitCheckout,
                Weight = 0.2
            },
        ];
        return Task.FromResult(steps);
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    /// <summary>
    /// 把仓库标识补全成可克隆的 URL：<c>owner/repo</c> → <c>https://github.com/owner/repo</c>。
    /// 已经是 URL / scp 形式（http、https、git、ssh、git@、file）的输入原样返回，
    /// 这样 GitLab、自建主机、本地路径都能直接用。
    /// 纯函数，public 以便测试工程直接覆盖（本工程未配置 InternalsVisibleTo）。
    /// </summary>
    public static string NormalizeUrl(string name)
    {
        var n = (name ?? "").Trim();
        if (n.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return n;
        }
        return "https://github.com/" + n.Trim('/');
    }

    /// <summary>
    /// 可选：给 GitHub 请求套上代理/镜像前缀。
    ///
    /// 背景：部分网络环境直连 github.com **会挂死**（真机实测：`git ls-remote` 直连超时，
    /// 而 <c>http://gh-proxy.org/</c> 前缀可用）。默认不启用 ——
    /// 不把第三方镜像写死进产品行为，只在用户显式设置
    /// <c>UMANGO_GITHUB_PROXY</c>（如 <c>http://gh-proxy.org/</c>）时生效。
    /// </summary>
    private static string ApplyProxy(string url)
    {
        var prefix = Environment.GetEnvironmentVariable(ProxyEnvVar);
        if (string.IsNullOrWhiteSpace(prefix)) return url;

        // 只代理 http(s) 形式的 GitHub 地址：scp 形式（git@github.com:...）与本地路径原样通过
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || !url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        prefix = prefix.Trim();
        if (!prefix.EndsWith('/')) prefix += "/";
        return prefix + url;
    }

    /// <summary>
    /// 极简 URL 编码（GitHub 搜索的 q 参数）。
    /// 只保留 RFC 3986 的 unreserved 字符，空格用 <c>+</c>（GitHub 官方示例就是 <c>q=tetris+language:assembly</c>），
    /// 其余按 UTF-8 逐字节百分号编码 —— 中文关键词（如"截图工具"）必须走这条路。
    /// </summary>
    private static string UrlEncode(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
                || b == '-' || b == '_' || b == '.' || b == '~')
            {
                sb.Append((char)b);
            }
            else if (b == ' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 宽松版本比较：任何字符串都能比较，不会失败。
    ///
    /// 规则（与核心层 LooseVersion 一致，另加一条针对真实 tag 的前缀剥离）：
    ///   · 先按段比较主版本，**数字段高于文本段** —— 否则 <c>nightly</c> 会靠 semver 的
    ///     预发布规则排到所有真实版本前面（曾出现的排序错乱）；
    ///   · <c>1.2</c> 与 <c>1.2.0</c> 视为相等（主段补齐到 3 段）；
    ///   · 有预发布段的版本低于没有的（1.0.0-rc &lt; 1.0.0）；
    ///   · <c>+build</c> 元数据按 semver 规范不参与比较；
    ///   · 前缀剥离（核心层没有的一步，见 <see cref="SplitVersion"/>）：把
    ///     <c>jq-1.7.1</c> 这类"名字前缀"剥掉再比较，否则它会被当成文本版本排到 1.6 之后。
    ///
    /// 纯函数，public 以便测试工程直接覆盖（本工程未配置 InternalsVisibleTo）。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var (mainA, preA) = SplitVersion(a);
        var (mainB, preB) = SplitVersion(b);

        var len = Math.Max(mainA.Count, mainB.Count);
        for (var i = 0; i < len; i++)
        {
            var pa = i < mainA.Count ? mainA[i] : VersionPart.Zero;
            var pb = i < mainB.Count ? mainB[i] : VersionPart.Zero;
            var c = pa.CompareMain(pb);
            if (c != 0) return c;
        }

        if (preA.Count == 0 && preB.Count == 0) return 0;
        if (preA.Count == 0) return 1;
        if (preB.Count == 0) return -1;

        var plen = Math.Max(preA.Count, preB.Count);
        for (var i = 0; i < plen; i++)
        {
            var hasA = i < preA.Count;
            var hasB = i < preB.Count;
            if (!hasA && !hasB) break;
            // 段数少的预发布版本更低：1.0.0-rc < 1.0.0-rc.1
            if (!hasA) return -1;
            if (!hasB) return 1;

            var c = preA[i].ComparePre(preB[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>
    /// 把版本串切成 [主版本段, 预发布段]。
    /// 会剥掉常见包装：refs/tags/ 前缀、任意 '/' 之前的部分、开头的 v/V、
    /// 首个数字之前的名字前缀（jq-1.7.1 → 1.7.1），以及 +build 元数据。
    /// </summary>
    private static (List<VersionPart> Main, List<VersionPart> Pre) SplitVersion(string? raw)
    {
        var body = (raw ?? "").Trim();

        var marker = body.LastIndexOf("refs/tags/", StringComparison.Ordinal);
        if (marker >= 0) body = body[(marker + "refs/tags/".Length)..];

        var slash = body.LastIndexOf('/');
        if (slash >= 0) body = body[(slash + 1)..];

        if (body.StartsWith('v') || body.StartsWith('V')) body = body[1..];

        // 前缀噪声剥离：真机样本（jq 仓库）的 tag 长这样 —— jq-1.7.1 / release-2.0 / 1.6rc2。
        // 若开头是字母，就把第一个数字之前的部分整段剥掉；否则 "jq-1.7.1" 会被切成
        // 「主段 = 文本 jq」，从而排到 "1.6rc2" **后面**（真机实测踩到，版本列表顺序错乱）。
        // 完全不含数字的 tag（nightly / latest）保持原样：它们不是版本号，应当排在所有版本之后。
        if (body.Length > 0 && char.IsAsciiLetter(body[0]))
        {
            var firstDigit = -1;
            for (var i = 0; i < body.Length; i++)
            {
                if (char.IsAsciiDigit(body[i]))
                {
                    firstDigit = i;
                    break;
                }
            }
            if (firstDigit > 0) body = body[firstDigit..];
        }

        // build metadata 不参与优先级比较
        var plus = body.IndexOf('+');
        if (plus >= 0) body = body[..plus];

        // 第一个 '-' 之后是预发布段
        string? pre = null;
        var dash = body.IndexOf('-');
        if (dash >= 0)
        {
            pre = body[(dash + 1)..];
            body = body[..dash];
        }

        var main = SplitParts(body);
        // "1.2" 与 "1.2.0" 语义相同 → 补齐到 3 段
        while (main.Count < 3) main.Add(VersionPart.Zero);

        return (main, pre is null ? new List<VersionPart>() : SplitParts(pre));
    }

    private static List<VersionPart> SplitParts(string s)
    {
        var parts = new List<VersionPart>();
        foreach (var token in s.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            // 粘连的字母/数字再切一刀：jq 的 tag 里 "1.7rc2" 实际是 1.7 + rc + 2。
            // 不切的话整段 "7rc2" 会被判成文本段，于是这些预发布版全被排到 1.0 之后
            // （真机样本实测到的顺序错乱），切完就能正确穿插在 1.7 与 1.6 之间。
            var start = 0;
            for (var i = 1; i < token.Length; i++)
            {
                if (char.IsAsciiDigit(token[i]) != char.IsAsciiDigit(token[i - 1]))
                {
                    parts.Add(VersionPart.Parse(token[start..i]));
                    start = i;
                }
            }
            parts.Add(VersionPart.Parse(token[start..]));
        }
        return parts;
    }

    /// <summary>版本段：要么是数字段，要么是文本段</summary>
    private readonly struct VersionPart
    {
        private VersionPart(long number, string? text)
        {
            Number = number;
            Text = text;
        }

        public long Number { get; }
        public string? Text { get; }
        public bool IsNumber => Text is null;

        public static VersionPart Zero { get; } = new(0, null);

        /// <summary>
        /// 解析单个段。纯数字（且不超长）算数字段 ——
        /// "01" 在 semver 里非法，但在 git tag / winget 版本里很常见，按数字处理更符合直觉；
        /// 超过 18 位的数字按文本处理，避免溢出后排序错乱。
        /// </summary>
        public static VersionPart Parse(string token)
        {
            if (token.Length is > 0 and <= 18
                && token.All(char.IsAsciiDigit)
                && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return new VersionPart(n, null);
            }
            return new VersionPart(0, token.ToLowerInvariant());
        }

        /// <summary>主版本段：**数字段高于文本段**（nightly / latest 这类浮动 tag 排在真实版本之后）</summary>
        public int CompareMain(VersionPart other) => (IsNumber, other.IsNumber) switch
        {
            (true, true) => Number.CompareTo(other.Number),
            (true, false) => 1,
            (false, true) => -1,
            _ => string.CompareOrdinal(Text, other.Text)
        };

        /// <summary>预发布段遵循 semver：**数字段低于文本段**（1.0.0-1 &lt; 1.0.0-alpha）</summary>
        public int ComparePre(VersionPart other) => (IsNumber, other.IsNumber) switch
        {
            (true, true) => Number.CompareTo(other.Number),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(Text, other.Text)
        };
    }
}
