using System.Text.Json;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers.Adapters;

/// <summary>
/// npm 适配器。六种适配器的**参考实现**，其余适配器照此模式编写。
/// </summary>
public sealed class NpmPackageManager : CliPackageManagerBase
{
    public override PackageSourceKind Kind => PackageSourceKind.Npm;

    public override SourceCapabilities Capabilities => new(
        Search: true, ListVersions: true, ResolveDependencies: true,
        OfficialDownloadCounts: true, Install: true, Uninstall: true, Upgrade: true);

    public override string InstallHint =>
        "npm 随 Node.js 一起安装：https://nodejs.org（也可用 nvm / fnm 管理版本）";

    /// <summary>npm 全局包目录按平台惯例不同</summary>
    protected override string DefaultInstallDirectory => SystemInfo.Platform switch
    {
        PlatformKind.Windows => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
        PlatformKind.MacOs => "/usr/local/lib/node_modules",
        _ => Path.Combine(SystemInfo.HomeDirectory, ".local", "lib", "node_modules")
    };

    /// <summary>`npm root -g` 是最权威的全局安装目录</summary>
    protected override string[]? InstallDirArgs => ["root", "-g"];

    public override async Task<IReadOnlyList<PackageInfo>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var output = await RunCliAsync(
            ["search", query, "--json", "--searchlimit", limit.ToString()], null, null, ct)
            .ConfigureAwait(false);

        if (!output.Success && LooksLikeNotFound(output.StdErr, output.StdOut))
            throw new ProcessException($"找不到与 {query} 匹配的包", "package_not_found", output.StdErr, output.ExitCode);
        output.EnsureSuccess($"npm search {query}");

        using var doc = ParseJson<JsonDocument>(output.StdOut, "npm search")
                        ?? throw new ProcessException("npm search 未返回内容", "parse_error", "", 0);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        var list = new List<PackageInfo>();
        foreach (var item in doc.RootElement.EnumerateArray().Take(limit))
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new PackageInfo
            {
                Source = Kind,
                Name = name,
                Summary = item.TryGetProperty("description", out var d) ? d.GetString() : null,
                LatestVersion = item.TryGetProperty("version", out var v) ? v.GetString() : null,
                License = item.TryGetProperty("license", out var l) ? l.GetString() : null,
            });
        }
        return list;
    }

    public override async Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(
        string name, CancellationToken ct = default)
    {
        var output = await RunCliAsync(["view", name, "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success)
        {
            if (LooksLikeNotFound(output.StdErr, output.StdOut))
                throw new ProcessException($"找不到包 {name}", "package_not_found", output.StdErr, output.ExitCode);
            output.EnsureSuccess($"npm view {name}");
        }

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"npm view {name}")
                        ?? throw new ProcessException($"找不到包 {name}", "package_not_found", "", 0);

        var versions = new List<PackageVersion>();
        var root = doc.RootElement;

        // npm view 的 packument：versions 可能是对象映射，也可能是数组
        if (root.TryGetProperty("versions", out var vers))
        {
            IEnumerable<string> names = vers.ValueKind switch
            {
                JsonValueKind.Object => vers.EnumerateObject().Select(p => p.Name),
                JsonValueKind.Array => vers.EnumerateArray()
                                           .Select(e => e.GetString() ?? "").Where(s => s.Length > 0),
                JsonValueKind.String => [vers.GetString() ?? ""],
                _ => []
            };
            foreach (var v in names)
                versions.Add(new PackageVersion { Version = v, IsPrerelease = IsPrerelease(v) });
        }

        if (versions.Count == 0 && root.TryGetProperty("version", out var single))
        {
            var v = single.GetString();
            if (!string.IsNullOrEmpty(v))
                versions.Add(new PackageVersion { Version = v, IsPrerelease = IsPrerelease(v) });
        }

        if (versions.Count == 0)
            throw new ProcessException($"找不到包 {name}", "package_not_found", "", 0);

        return versions.OrderByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override async Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default)
    {
        var target = string.IsNullOrEmpty(version) ? name : $"{name}@{version}";
        var output = await RunCliAsync(["view", target, "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        using var doc = ParseJson<JsonDocument>(output.StdOut, $"npm view {target}");
        if (doc is null) return [];

        var list = new List<DependencyRef>();
        void Collect(string prop, bool isDev)
        {
            if (!doc.RootElement.TryGetProperty(prop, out var deps)) return;
            if (deps.ValueKind != JsonValueKind.Object) return;
            foreach (var p in deps.EnumerateObject())
            {
                var req = p.Value.GetString() ?? "*";
                list.Add(new DependencyRef(p.Name, req, isDev));
            }
        }
        Collect("dependencies", false);
        Collect("peerDependencies", false);
        Collect("optionalDependencies", false);
        return list;
    }


    /// <summary>
    /// 列出全局已安装的包。
    /// **命令与输出格式取自真机实测**：`npm list -g --depth=0 --json` 返回
    /// <code>
    /// { "name": "lib", "dependencies": { "包名": { "version": "1.0.0", "overridden": false }, ... } }
    /// </code>
    /// depth=0 保证只列顶层（不递归依赖树），否则输出会爆炸。
    /// </summary>
    public override async Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await RunCliAsync(["list", "-g", "--depth=0", "--json"], null, null, ct).ConfigureAwait(false);
        if (!output.Success) return [];

        using var doc = ParseJson<JsonDocument>(output.StdOut, "npm list -g");
        if (doc is null) return [];

        var list = new List<InstalledPackage>();
        // 有的 registry/版本会把结果包在 dependencies 里，也有的直接给对象
        var deps = doc.RootElement.TryGetProperty("dependencies", out var d) ? d : doc.RootElement;
        if (deps.ValueKind != JsonValueKind.Object) return [];

        foreach (var dep in deps.EnumerateObject())
        {
            var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
            list.Add(new InstalledPackage { Source = Kind, Name = dep.Name, Version = version });
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// npm 的安装按下载方式拆成四步 —— 这就是进度界面上要逐项显示的子项。
    /// </summary>
    public override Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default)
    {
        IReadOnlyList<InstallStep> steps =
        [
            new InstallStep { Title = "解析依赖", Method = DownloadMethod.DependencyResolve, Weight = 0.2 },
            new InstallStep { Title = "读取注册表元数据", Method = DownloadMethod.RegistryMetadata, Weight = 0.1 },
            new InstallStep { Title = "下载压缩包", Method = DownloadMethod.RegistryTarball, Weight = 0.6 },
            new InstallStep { Title = "链接与写入", Method = DownloadMethod.LinkWrite, Weight = 0.1 },
        ];
        return Task.FromResult(steps);
    }

    /// <summary>粗略判断是否预发布版本（含 - 且带字母标记）</summary>
    private static bool IsPrerelease(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return false;
        var pre = version[(dash + 1)..].ToLowerInvariant();
        return pre.Any(char.IsLetter);
    }
}
