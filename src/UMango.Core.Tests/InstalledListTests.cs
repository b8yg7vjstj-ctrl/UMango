using UMango.Core.PackageManagers;
using UMango.Core.PackageManagers.Adapters;
using UMango.Core.Platform;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 「应用列表读取」测试（需求：参考 UnigetUI 的做法，按包管理器各自列出已安装项）。
///
/// 其中 npm / Homebrew 两项是**真机验证**（本机装了这两个 CLI，且确实有已安装的包）；
/// scoop / winget 无法在 macOS 上验证，只断言"平台门控生效、不执行命令"。
/// </summary>
[Trait("Category", "真机")]
public class InstalledListTests
{
    [Fact]
    public async Task npm能列出全局已安装的包_真机()
    {
        var npm = new NpmPackageManager();
        if (!await IsAvailableAsync(npm)) { Console.WriteLine("跳过：npm 不可用"); return; }

        var list = await npm.ListInstalledAsync();

        Assert.NotEmpty(list);
        Assert.All(list, p => Assert.Equal(PackageSourceKind.Npm, p.Source));
        Assert.All(list, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));

        // npm 自己一定是全局包之一 —— 这是个稳定的不变量
        Assert.Contains(list, p => p.Name == "npm");
        // 版本必须解析出来（实测 npm list -g --depth=0 --json 会给 version 字段）
        Assert.Contains(list, p => p.Name == "npm" && !string.IsNullOrWhiteSpace(p.Version));
        // depth=0：不应当出现深层依赖（否则说明参数没生效）
        Assert.True(list.Count < 200, $"条目数异常偏多（{list.Count}），可能 depth=0 没生效");
    }

    [Fact]
    public async Task homebrew能列出已安装的formula_真机()
    {
        var brew = new HomebrewPackageManager();
        if (!await IsAvailableAsync(brew)) { Console.WriteLine("跳过：brew 不可用"); return; }

        var list = await brew.ListInstalledAsync();

        Assert.NotEmpty(list);
        Assert.All(list, p => Assert.Equal(PackageSourceKind.Homebrew, p.Source));

        // 本机装了 aria2（下载器要用它），这是个稳定断言
        Assert.Contains(list, p => p.Name == "aria2");

        // 实测 brew list --formula --versions 会在同一行给出多个版本，
        // 第二个及以后的版本必须落到 OtherVersions 而不是被丢掉或混进 Version
        Assert.All(list, p =>
        {
            if (p.Version is not null) Assert.DoesNotContain(' ', p.Version);
        });
        Assert.True(list.Count > 10, $"本机应有较多 formula，实际 {list.Count}");
    }

    [Fact]
    public async Task pnpm能列出全局已安装的包_真机且允许为空()
    {
        var pnpm = new PnpmPackageManager();
        if (!await IsAvailableAsync(pnpm)) { Console.WriteLine("跳过：pnpm 不可用"); return; }

        // pnpm 返回的是**数组**（与 npm 的对象不同）；空依赖也不应抛异常
        var list = await pnpm.ListInstalledAsync();
        Assert.All(list, p => Assert.Equal(PackageSourceKind.Pnpm, p.Source));
    }

    [Fact]
    public async Task git按本地仓库扫描而不是声称已安装()
    {
        var root = Path.Combine(Path.GetTempPath(), $"umango-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "repo-a", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "nested", "repo-b", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "not-a-repo"));

        try
        {
            // 直接测扫描函数（public static）：不往用户默认目录里造数据，
            // 也不需要为了测试去破坏 GitPackageManager 的 sealed 约定。
            var repos = GitPackageManager.ScanLocalRepositories(root);

            var names = repos.Select(Path.GetFileName).ToList();
            Assert.Contains("repo-a", names);
            Assert.Contains("repo-b", names);       // 深度 2 内要能找到
            Assert.DoesNotContain("not-a-repo", names);
            Assert.All(repos, r => Assert.True(Directory.Exists(r)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task 平台不支持时任何CLI调用都被阻止且不起进程()
    {
        // macOS 上 winget/scoop 不可用。这里断言的不是"返回空"，
        // 而是**抛出结构化的 unsupported_platform** —— 且真实原因是它没去起进程。
        if (SystemInfo.IsWindows) return;

        foreach (var pm in new IPackageManager[] { new WingetPackageManager(), new ScoopPackageManager() })
        {
            // 只对「该适配器声明支持、因而会真正调用 CLI」的能力断言门控。
            // 不支持的能力（例如 winget 的依赖解析）由基类直接返回空，本就不会起进程 ——
            // 那不是门控漏洞，混进来断言反而会把测试写错（第一版就错在这里）。
            var caps = pm.Capabilities;
            var calls = new List<Func<Task>>();
            if (caps.Search) calls.Add(() => pm.SearchAsync("x", 5));
            if (caps.ListVersions) calls.Add(() => pm.GetVersionsAsync("x"));
            if (caps.ListInstalled) calls.Add(() => pm.ListInstalledAsync());
            if (caps.ResolveDependencies) calls.Add(() => pm.GetDependenciesAsync("x", null));

            Assert.NotEmpty(calls);   // 至少要验证一条路径

            foreach (var call in calls)
            {
                var ex = await Assert.ThrowsAsync<ProcessException>(call);
                Assert.Equal("unsupported_platform", ex.Code);
                // 关键：错误信息里应提到"已阻止执行"，证明没起进程
                Assert.Contains("已阻止执行", ex.Message);
            }
        }
    }

    [Fact]
    public void 能力位默认支持列已安装且可单独关闭()
    {
        var defaultCaps = new SourceCapabilities(true, true, false, false, true, true, true);
        Assert.True(defaultCaps.ListInstalled, "默认应支持列出已安装");

        var off = defaultCaps with { ListInstalled = false };
        Assert.False(off.ListInstalled);
        // 关掉这一个能力不应影响其它能力位（init-only 属性的意义）
        Assert.True(off.Search);
        Assert.True(off.Install);
    }

    private static async Task<bool> IsAvailableAsync(IPackageManager pm)
    {
        var a = await pm.ProbeAsync();
        return a.State is AvailabilityState.Available or AvailabilityState.Degraded;
    }
}
