using UMango.Core.Caching;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 包源可用性持久化缓存的测试。
///
/// **要修的缺陷**：原先可用性只存在注册表的内存字典里，重启即失效 ——
/// 也就是每次启动应用都要把六个包源重新探测一遍（最多 12 次进程启动），
/// 而可用性一天之内几乎不变。
///
/// 核心断言是"第二次读不再探测"：用可计数的假适配器直接验证**没有起进程**，
/// 而不是只看返回值的状态（那无法区分"用了缓存"和"又探了一遍但结果相同"）。
/// </summary>
[Trait("Category", "接线")]
public class AvailabilityCacheTests
{
    /// <summary>可计数的假适配器：ProbeCount 就是"起了几次进程"的代理指标。</summary>
    private sealed class CountingAdapter(PackageSourceKind kind) : IPackageManager
    {
        public int ProbeCount { get; private set; }
        public bool ShouldThrow { get; set; }
        public string? LastSeededDirectory { get; private set; }
        public string InstallDirectory => LastSeededDirectory ?? "/default/path";

        public PackageSourceKind Kind => kind;
        public string DisplayName => kind.DisplayName();
        public string CliName => kind.Id();
        public IReadOnlyList<PlatformKind> SupportedPlatforms => kind.SupportedPlatforms();
        public SourceCapabilities Capabilities => SourceCapabilities.None;
        public string InstallHint => "测试用";
        public string? VerificationNote => null;

        public Task<SourceAvailability> ProbeAsync(CancellationToken ct = default)
        {
            ProbeCount++;
            if (ShouldThrow) throw new InvalidOperationException("CLI 被占用");
            return Task.FromResult(new SourceAvailability
            {
                Kind = kind,
                State = AvailabilityState.Available,
                CliFound = true,
                CliPath = "/usr/bin/" + CliName,
                CliVersion = "1.2.3",
                ResolvedInstallDirectory = "/real/install/path"
            });
        }

        public void SeedResolvedInstallDirectory(string? directory) => LastSeededDirectory = directory;

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string q, int l, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageInfo>>([]);
        public Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(string n, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PackageVersion>>([]);
        public Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(string n, string? v, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DependencyRef>>([]);
        public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstalledPackage>>([]);
        public Task<IReadOnlyList<InstallStep>> PlanInstallAsync(string n, string? v, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstallStep>>([]);
    }

    // ================= 基本行为 =================

    [Fact]
    public async Task 首次探测后写入缓存()
    {
        var store = new MemoryAvailabilityCache();
        var adapter = new CountingAdapter(PackageSourceKind.Npm);
        var registry = new PackageManagerRegistry([adapter], store);

        var result = await registry.GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(AvailabilityState.Available, result.State);
        Assert.Equal(1, adapter.ProbeCount);
        Assert.True(store.SaveCount > 0, "探测结果应当写入缓存");
        Assert.Single(store.Load());
    }

    [Fact]
    public async Task 同一个注册表内重复取值不再探测()
    {
        var adapter = new CountingAdapter(PackageSourceKind.Npm);
        var registry = new PackageManagerRegistry([adapter], new MemoryAvailabilityCache());

        await registry.GetAvailabilityAsync(PackageSourceKind.Npm);
        await registry.GetAvailabilityAsync(PackageSourceKind.Npm);
        await registry.GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(1, adapter.ProbeCount);
    }

    [Fact]
    public async Task 缓存命中时不起进程_这是本缺陷的核心()
    {
        var store = new MemoryAvailabilityCache();

        // 第一次"启动"：探测并落盘
        var first = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([first], store).GetAvailabilityAsync(PackageSourceKind.Npm);
        Assert.Equal(1, first.ProbeCount);

        // 第二次"启动"：全新注册表与适配器，应完全走缓存
        var second = new CountingAdapter(PackageSourceKind.Npm);
        var result = await new PackageManagerRegistry([second], store)
            .GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(0, second.ProbeCount);          // ★ 没有再起进程
        Assert.Equal(AvailabilityState.Available, result.State);
        Assert.Equal("1.2.3", result.CliVersion);    // 缓存里的内容照常读回
    }

    [Fact]
    public async Task 缓存命中时安装目录被补种_否则子标题会退回默认值()
    {
        var store = new MemoryAvailabilityCache();

        var first = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([first], store).GetAvailabilityAsync(PackageSourceKind.Npm);

        // 需求要求左栏子标题显示**真实**安装目录。
        // 若缓存只存"可用不可用"，缓存命中时这里就会退回 /default/path。
        var second = new CountingAdapter(PackageSourceKind.Npm);
        new PackageManagerRegistry([second], store);

        Assert.Equal("/real/install/path", second.LastSeededDirectory);
        Assert.Equal("/real/install/path", second.InstallDirectory);
    }

    // ================= 有效期 =================

    [Fact]
    public async Task 缓存过期后会重新探测()
    {
        var store = new MemoryAvailabilityCache();
        var first = new CountingAdapter(PackageSourceKind.Npm);

        // TTL 设成 0：任何缓存都立即过期
        await new PackageManagerRegistry([first], store, TimeSpan.Zero)
            .GetAvailabilityAsync(PackageSourceKind.Npm);

        var second = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([second], store, TimeSpan.Zero)
            .GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(1, second.ProbeCount);
    }

    [Fact]
    public async Task refresh为true时强制重新探测()
    {
        var adapter = new CountingAdapter(PackageSourceKind.Npm);
        var registry = new PackageManagerRegistry([adapter], new MemoryAvailabilityCache());

        await registry.GetAvailabilityAsync(PackageSourceKind.Npm);
        await registry.GetAvailabilityAsync(PackageSourceKind.Npm, refresh: true);

        Assert.Equal(2, adapter.ProbeCount);   // 用户点了「刷新包源」就该真的重探
    }

    [Fact]
    public async Task 默认有效期为24小时()
    {
        Assert.Equal(TimeSpan.FromHours(24), PackageManagerRegistry.DefaultTtl);

        var store = new MemoryAvailabilityCache();
        var adapter = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([adapter], store).GetAvailabilityAsync(PackageSourceKind.Npm);

        // 默认 TTL 下，第二次不该重探
        var second = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([second], store).GetAvailabilityAsync(PackageSourceKind.Npm);
        Assert.Equal(0, second.ProbeCount);
    }

    // ================= 失败降级 =================

    [Fact]
    public async Task 探测抛异常时降级到上次结果而不是抹掉包源()
    {
        var store = new MemoryAvailabilityCache();
        var good = new CountingAdapter(PackageSourceKind.Npm);
        await new PackageManagerRegistry([good], store).GetAvailabilityAsync(PackageSourceKind.Npm);

        // 第二次探测失败，且 TTL 设成 0 强制它去探
        var bad = new CountingAdapter(PackageSourceKind.Npm) { ShouldThrow = true };
        var result = await new PackageManagerRegistry([bad], store, TimeSpan.Zero)
            .GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(1, bad.ProbeCount);
        // "上次 npm 是可用的"远比"这次什么都没有"对用户有用
        Assert.Equal(AvailabilityState.Degraded, result.State);
        Assert.Contains("本次探测失败", result.Detail);
        Assert.Contains("上次", result.Detail);
    }

    [Fact]
    public async Task 没有历史结果时探测失败标记为不可达()
    {
        var adapter = new CountingAdapter(PackageSourceKind.Npm) { ShouldThrow = true };
        var registry = new PackageManagerRegistry([adapter], new MemoryAvailabilityCache());

        var result = await registry.GetAvailabilityAsync(PackageSourceKind.Npm);

        Assert.Equal(AvailabilityState.Unreachable, result.State);
        Assert.Contains("CLI 被占用", result.Detail);
    }

    // ================= 平台门控与缓存的关系 =================

    [Fact]
    public async Task 平台不支持的包源仍然不探测也不写缓存()
    {
        // 门控必须优先于缓存：不能因为缓存里有记录就去碰 CLI
        var kind = SystemInfo.IsWindows ? PackageSourceKind.Homebrew : PackageSourceKind.Winget;
        var adapter = new CountingAdapter(kind);
        var store = new MemoryAvailabilityCache();
        var registry = new PackageManagerRegistry([adapter], store);

        var result = await registry.GetAvailabilityAsync(kind);

        Assert.Equal(AvailabilityState.UnsupportedPlatform, result.State);
        Assert.Equal(0, adapter.ProbeCount);
        Assert.Equal(0, store.SaveCount);
    }

    // ================= JSON 实现 =================

    [Fact]
    public void JSON缓存往返_含中文与枚举()
    {
        var path = Path.Combine(Path.GetTempPath(), $"umango-cache-{Guid.NewGuid():N}.json");
        try
        {
            var cache = new JsonAvailabilityCache(path);
            cache.Save([
                new CachedAvailability
                {
                    Kind = PackageSourceKind.Homebrew,
                    State = AvailabilityState.Degraded,
                    CliFound = true,
                    CliVersion = "6.0.22",
                    Detail = "中文说明：未真机验证",
                    ResolvedInstallDirectory = "/opt/homebrew",
                    VerificationNote = "未在真机验证",
                    CheckedAt = DateTimeOffset.Now
                }
            ]);

            Assert.True(File.Exists(path));
            var loaded = cache.Load();

            var entry = Assert.Single(loaded);
            Assert.Equal(PackageSourceKind.Homebrew, entry.Kind);
            Assert.Equal(AvailabilityState.Degraded, entry.State);
            Assert.Equal("/opt/homebrew", entry.ResolvedInstallDirectory);
            Assert.Contains("中文说明", entry.Detail);
            Assert.Contains("未在真机验证", entry.VerificationNote);
        }
        finally
        {
            try { File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 缓存文件损坏时当作没有缓存而不是抛异常()
    {
        var path = Path.Combine(Path.GetTempPath(), $"umango-cache-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON");
            var cache = new JsonAvailabilityCache(path);

            var ex = Record.Exception(() =>
            {
                var loaded = cache.Load();
                Assert.Empty(loaded);
            });
            Assert.Null(ex);
        }
        finally
        {
            try { File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 写入不可用路径时静默降级()
    {
        var root = Path.Combine(Path.GetTempPath(), $"umango-blk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker.txt");
        File.WriteAllText(blocker, "我是文件");

        try
        {
            var cache = new JsonAvailabilityCache(Path.Combine(blocker, "sub", "cache.json"));
            var ex = Record.Exception(() => cache.Save([]));
            Assert.Null(ex);   // 落盘失败不该影响业务
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void 单条记录的过期判定()
    {
        var ttl = TimeSpan.FromHours(24);
        var now = DateTimeOffset.Now;

        var fresh = new CachedAvailability
        {
            Kind = PackageSourceKind.Npm, State = AvailabilityState.Available, CheckedAt = now.AddHours(-1)
        };
        var old = new CachedAvailability
        {
            Kind = PackageSourceKind.Npm, State = AvailabilityState.Available, CheckedAt = now.AddHours(-25)
        };

        Assert.False(fresh.IsExpired(now, ttl));
        Assert.True(old.IsExpired(now, ttl));
    }
}
