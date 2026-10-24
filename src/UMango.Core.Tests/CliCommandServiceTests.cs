using UMango.Core.App;
using UMango.Core.Cli;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 命令行命令服务的测试。
///
/// 被测对象在 Core（平台中立），因此这些用例在 macOS 上也能跑 ——
/// 不需要 WPF 窗口，也不需要真实 CLI。
/// </summary>
[Collection("静态状态")]
public sealed class CliCommandServiceTests
{
    /// <summary>
    /// 可控的假适配器：能指定搜索/已安装结果，也能指定是否在当前平台可用。
    /// </summary>
    private sealed class StubAdapter : IPackageManager
    {
        public StubAdapter(PackageSourceKind kind, bool usable = true)
        {
            Kind = kind;
            Usable = usable;
        }

        public bool Usable { get; }
        public List<string> SearchQueries { get; } = [];
        public List<string> InstalledNames { get; } = [];

        public PackageSourceKind Kind { get; }
        public string DisplayName => Kind.DisplayName();
        public string InstallDirectory => "/fake/" + Kind.Id();
        public string CliName => Kind.Id();
        public IReadOnlyList<PlatformKind> SupportedPlatforms => Kind.SupportedPlatforms();
        public SourceCapabilities Capabilities => SourceCapabilities.None;
        public string InstallHint => "测试用";
        public string? VerificationNote => null;

        public Task<SourceAvailability> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new SourceAvailability
            {
                Kind = Kind,
                State = Usable ? AvailabilityState.Available : AvailabilityState.NotInstalled,
                CliFound = Usable
            });

        public void SeedResolvedInstallDirectory(string? directory) { }

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, int limit, CancellationToken ct = default)
        {
            SearchQueries.Add(query);
            return Task.FromResult<IReadOnlyList<PackageInfo>>([
                new PackageInfo { Source = Kind, Name = query + "-pkg", LatestVersion = "1.0.0", Summary = "简介" }
            ]);
        }

        public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackage>>(
                InstalledNames.Select(n => new InstalledPackage { Source = Kind, Name = n, Version = "1.2.3" }).ToList());

        public Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PackageVersion>>([]);

        public Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(string name, string? version, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DependencyRef>>([]);

        public Task<IReadOnlyList<InstallStep>> PlanInstallAsync(string name, string? version, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InstallStep>>([]);
    }

    private static CliCommandService NewService(params IPackageManager[] adapters) =>
        new(new PackageManagerRegistry(adapters));

    // ================= 基本路由 =================

    [Fact]
    public async Task 空参数返回帮助()
    {
        var result = await NewService().ExecuteAsync([]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("用法：umango"));
    }

    [Fact]
    public async Task 未知命令返回帮助而不是报错()
    {
        var result = await NewService().ExecuteAsync(["frobnicate"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("用法：umango"));
    }

    [Fact]
    public async Task help_子命令直接给帮助()
    {
        var result = await NewService().ExecuteAsync(["help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("install <包名>"));
    }

    // ================= search =================

    [Fact]
    public async Task 搜索会把关键词交给包管理器()
    {
        var adapter = new StubAdapter(PackageSourceKind.Npm);
        var result = await NewService(adapter).ExecuteAsync(["search", "vite"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("vite", adapter.SearchQueries.Single());
        Assert.Contains(result.Lines, l => l.Contains("vite-pkg"));
    }

    [Fact]
    public async Task 搜索缺关键词时给出用法并以非零码退出()
    {
        var result = await NewService(new StubAdapter(PackageSourceKind.Npm)).ExecuteAsync(["search"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("用法：umango search"));
    }

    [Fact]
    public async Task 搜索可以用source参数指定包源()
    {
        var npm = new StubAdapter(PackageSourceKind.Npm);
        var pnpm = new StubAdapter(PackageSourceKind.Pnpm);

        await NewService(npm, pnpm).ExecuteAsync(["search", "vite", "--source", "pnpm"]);

        Assert.Empty(npm.SearchQueries);
        Assert.Equal("vite", pnpm.SearchQueries.Single());
    }

    [Fact]
    public async Task 搜索用无法识别的包源时报错而不是静默换源()
    {
        var npm = new StubAdapter(PackageSourceKind.Npm);

        var result = await NewService(npm).ExecuteAsync(["search", "vite", "--source", "cargo"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(npm.SearchQueries);
        Assert.Contains(result.Lines, l => l.Contains("无法识别的包源"));
    }

    [Fact]
    public async Task 本机没有可用包源时说的是另一句话()
    {
        // 「用户指定了一个不认识的包源」和「本机压根没有可用包源」是两种情况，
        // 提示语必须能让人知道下一步该做什么：前者改名字，后者去装包管理器。
        //
        // 注意「本机没有可用包源」判的是**平台支持**（UsableOnHost 按
        // PackageSourceKind.SupportedPlatforms 过滤），不是适配器的可用性标记。
        // 所以这里要挑一个当前平台不支持的包源种类。
        var unsupported = Enum.GetValues<PackageSourceKind>()
            .First(k => !k.SupportedPlatforms().Contains(SystemInfo.Platform));

        var result = await NewService(new StubAdapter(unsupported)).ExecuteAsync(["search", "vite"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("本机没有可用的包源"));
        Assert.DoesNotContain(result.Lines, l => l.Contains("无法识别的包源"));
    }

    // ================= list =================

    [Fact]
    public async Task 列出已安装包()
    {
        var adapter = new StubAdapter(PackageSourceKind.Npm);
        adapter.InstalledNames.Add("vite");

        var result = await NewService(adapter).ExecuteAsync(["list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("vite"));
        Assert.Contains(result.Lines, l => l.Contains("1.2.3"));
    }

    // ================= source =================

    [Fact]
    public async Task source_列出全部包源及其可用性()
    {
        var npm = new StubAdapter(PackageSourceKind.Npm);
        var scoop = new StubAdapter(PackageSourceKind.Scoop, usable: false);

        var result = await NewService(npm, scoop).ExecuteAsync(["source"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("npm"));
        Assert.Contains(result.Lines, l => l.Contains("Scoop"));
        // 不可用的包源也要出现 —— 用户需要知道"它在这里但没装"
        Assert.Contains(result.Lines, l => l.Contains("/fake/scoop"));
    }

    // ================= install / uninstall / upgrade =================

    [Fact]
    public async Task 安装缺包名时给出用法并以非零码退出()
    {
        var result = await NewService(new StubAdapter(PackageSourceKind.Npm)).ExecuteAsync(["install"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("用法：umango install"));
    }

    [Fact]
    public async Task 卸载未实现的包时如实说明而不是假装成功()
    {
        var adapter = new StubAdapter(PackageSourceKind.Npm);
        adapter.InstalledNames.Add("vite");

        var result = await NewService(adapter).ExecuteAsync(["uninstall", "vite"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("尚未在命令行实现"));
    }

    [Fact]
    public async Task 卸载未安装的包时说明没找到()
    {
        var adapter = new StubAdapter(PackageSourceKind.Npm);

        var result = await NewService(adapter).ExecuteAsync(["uninstall", "不存在的包"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("没有找到已安装"));
    }

    [Fact]
    public async Task 升级未实现的命令如实说明()
    {
        var result = await NewService(new StubAdapter(PackageSourceKind.Npm)).ExecuteAsync(["upgrade", "vite"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("尚未在命令行实现"));
    }

    // ================= 与自更新助手协议共存 =================

    [Fact]
    public void 更新助手命令不在普通子命令表里()
    {
        Assert.DoesNotContain(UmangoCommandLine.Subcommands,
            s => string.Equals(s.CommandText, "update", StringComparison.Ordinal));
        Assert.True(UmangoCommandLine.IsReserved("update"));
        Assert.True(UmangoCommandLine.IsReserved("update_finished"));
        Assert.True(UmangoCommandLine.IsReserved("update_failed"));
    }

    [Fact]
    public void 更新助手命令被完整让给助手协议()
    {
        // 助手协议的参数里含有字面量 "source"（源安装包路径），
        // 而 UMango 自己也有一个叫 source 的子命令 ——
        // 若不在入口处分流，"source" 会被吃成子命令，解析结果与助手协议不一致。
        var parsed = UmangoCommandLine.TryParse(["update", "1234", "target", "source", "true"], out var line);

        Assert.False(parsed);
        Assert.Null(line);
    }

    [Fact]
    public void 助手协议的所有命令词都被让出去()
    {
        foreach (var reserved in UmangoCommandLine.ReservedCommandNames)
        {
            Assert.False(UmangoCommandLine.TryParse([reserved, "x"], out var line), reserved);
            Assert.Null(line);
        }
    }
}
