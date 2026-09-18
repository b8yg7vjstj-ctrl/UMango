using UMango.Core.Caching;
using UMango.Core.Platform;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 包管理器注册表：集中处理**平台门控**与调度。
/// 注册表注册全部适配器（不做注册期过滤），由 SupportedPlatforms 决定谁可用 ——
/// 这样界面才能表达"该包源在当前系统不可用"，而不是让它凭空消失。
/// </summary>
public sealed class PackageManagerRegistry
{
    /// <summary>默认有效期：24 小时。可用性的变化概率极低，而探测代价是起进程。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly List<IPackageManager> _managers;
    private readonly Dictionary<PackageSourceKind, CachedAvailability> _cache = [];
    private readonly IAvailabilityCache? _store;
    private readonly TimeSpan _ttl;

    /// <param name="managers">全部适配器</param>
    /// <param name="cache">持久化缓存；为 null 则退回纯内存（进程内仍然缓存）</param>
    /// <param name="ttl">有效期，默认 24 小时</param>
    public PackageManagerRegistry(IEnumerable<IPackageManager> managers,
                                  IAvailabilityCache? cache = null,
                                  TimeSpan? ttl = null)
    {
        _managers = managers.ToList();
        _store = cache;
        _ttl = ttl ?? DefaultTtl;

        RestoreFromCache();
    }

    /// <summary>
    /// 从持久化缓存恢复。
    ///
    /// **过期的记录也照收**：它有两个用处 ——
    ///   ① 探测失败时降级返回（"上次是好的"比"什么都没有"有用得多）；
    ///   ② 补种已解析的安装目录（否则子标题会退回平台默认值）。
    /// 是否过期只在 <see cref="GetAvailabilityAsync"/> 里决定要不要重新探测。
    /// </summary>
    private void RestoreFromCache()
    {
        if (_store is null) return;

        try
        {
            foreach (var entry in _store.Load())
            {
                _cache[entry.Kind] = entry;

                // 把解析过的安装目录补进适配器：这正是"缓存命中会丢子标题"的修法
                Find(entry.Kind)?.SeedResolvedInstallDirectory(entry.ResolvedInstallDirectory);
            }
        }
        catch (Exception)
        {
            // 缓存读不出来就当作没有：下次探测后会重新写入
        }
    }

    /// <summary>把当前缓存写入持久化存储。</summary>
    private void PersistCache()
    {
        if (_store is null) return;
        try
        {
            _store.Save(_cache.Values.ToList());
        }
        catch (Exception)
        {
            // 落盘失败不影响业务
        }
    }

    public IReadOnlyList<IPackageManager> All => _managers;

    public IPackageManager? Find(PackageSourceKind kind) =>
        _managers.FirstOrDefault(m => m.Kind == kind);

    /// <summary>当前平台上可用的包管理器</summary>
    public IEnumerable<IPackageManager> UsableOnHost =>
        _managers.Where(m => m.Kind.Supports(SystemInfo.Platform));

    /// <summary>带缓存的可用性探测（同一进程内只探一次，避免反复起 CLI）</summary>
    public async Task<SourceAvailability> GetAvailabilityAsync(
        PackageSourceKind kind, bool refresh = false, CancellationToken ct = default)
    {
        // ── 顺序很重要，三步的判断依据完全不同 ──
        //
        // 1) 平台门控必须最优先。平台支持是**静态事实**，不该由任何缓存决定：
        //    缓存文件若来自另一台机器（或用户迁移了配置），里面可能写着
        //    "winget 在 macOS 上可用" —— 放在缓存之后就会被直接采信、**绕过门控**。
        //    （这一点是被 AvailabilityCacheTests 的"平台不支持不探测"用例暴露的：
        //      当时的实现确实先查缓存，测试断言"不写缓存"时才发现顺序反了。）
        //    门控路径**不写缓存**：它的结果是推导出来的，不是探测出来的。
        // 2) 再查缓存：命中且未过期就返回，一个进程都不起（这正是缓存存在的意义）。
        // 3) 最后才真正探测。
        var m = Find(kind);
        if (m is null)
        {
            return new SourceAvailability
            {
                Kind = kind,
                State = AvailabilityState.UnsupportedPlatform,
                Detail = "未注册该包源适配器"
            };
        }

        if (!kind.Supports(SystemInfo.Platform))
        {
            // 注意仍要带上适配器自报的 VerificationNote —— 被门控的包源（macOS 上的 winget / scoop）
            // 恰恰也是"未在真机验证"的那些，门控路径若丢掉标注，界面就不会显示"未验证"提示。
            return new SourceAvailability
            {
                Kind = kind,
                State = AvailabilityState.UnsupportedPlatform,
                Detail = $"{kind.DisplayName()} 仅在 {string.Join(" / ", kind.SupportedPlatforms().Select(p => p.DisplayName()))} 上可用",
                VerificationNote = m.VerificationNote
            };
        }

        var now = DateTimeOffset.Now;
        if (!refresh && _cache.TryGetValue(kind, out var cached) && !cached.IsExpired(now, _ttl))
        {
            return cached.ToAvailability();
        }

        SourceAvailability result;
        try
        {
            result = await m.ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 探测抛异常（CLI 被占用、权限问题、适配器内部 bug）时降级到上次结果，
            // 而不是把包源从界面上抹掉 —— "上次 npm 是可用的"远比"这次什么都没有"有用。
            result = FallbackToStale(kind, ex.Message) ?? new SourceAvailability
            {
                Kind = kind,
                State = AvailabilityState.Unreachable,
                Detail = ex.Message
            };
        }

        _cache[kind] = new CachedAvailability
        {
            Kind = result.Kind,
            State = result.State,
            CliFound = result.CliFound,
            CliPath = result.CliPath,
            CliVersion = result.CliVersion,
            Detail = result.Detail,
            VerificationNote = result.VerificationNote,
            ResolvedInstallDirectory = result.ResolvedInstallDirectory,
            CheckedAt = result.CheckedAt
        };
        PersistCache();

        return result;
    }

    /// <summary>
    /// 探测失败时的降级：返回**上次已知**的结果并标注为 Degraded。
    ///
    /// 为什么值得这么做：网络抖动、CLI 临时被占用都会让一次探测失败，
    /// 而"上次 npm 是可用的"远比"这次什么都没有"对用户有用 ——
    /// 直接把包源从界面上抹掉是更糟的处理。
    /// </summary>
    private SourceAvailability? FallbackToStale(PackageSourceKind kind, string reason)
    {
        if (!_cache.TryGetValue(kind, out var stale)) return null;

        return new SourceAvailability
        {
            Kind = kind,
            State = AvailabilityState.Degraded,
            CliFound = stale.CliFound,
            CliPath = stale.CliPath,
            CliVersion = stale.CliVersion,
            VerificationNote = stale.VerificationNote,
            ResolvedInstallDirectory = stale.ResolvedInstallDirectory,
            CheckedAt = stale.CheckedAt,
            Detail = $"本次探测失败，显示上次结果（{stale.CheckedAt:MM-dd HH:mm}）：{reason}"
        };
    }

    /// <summary>包源分类视图：界面左栏直接用这个渲染（标题=包管理器名，子标题=安装目录）。</summary>
    public async Task<IReadOnlyList<PackageSourceCategory>> GetCategoriesAsync(
        bool refresh = false, CancellationToken ct = default)
    {
        var list = new List<PackageSourceCategory>();
        foreach (var m in _managers)
        {
            var av = await GetAvailabilityAsync(m.Kind, refresh, ct).ConfigureAwait(false);
            list.Add(new PackageSourceCategory
            {
                Kind = m.Kind,
                Title = m.DisplayName,
                Subtitle = m.InstallDirectory,   // ← 子标题 = 安装目录
                SupportedHere = m.Kind.Supports(SystemInfo.Platform),
                Available = av.IsUsable,
                AvailabilityState = av.State,
                VerificationNote = av.VerificationNote,
                Detail = av.Detail,
                Capabilities = m.Capabilities,
                InstallHint = m.InstallHint
            });
        }
        return list;
    }
}

/// <summary>
/// 包源分类项。**标题为包管理器名、子标题为安装目录** —— 直接对应需求。
/// </summary>
public sealed class PackageSourceCategory
{
    public required PackageSourceKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public bool SupportedHere { get; init; }
    public bool Available { get; init; }
    public AvailabilityState AvailabilityState { get; init; }
    public string? VerificationNote { get; init; }
    public string? Detail { get; init; }
    public SourceCapabilities Capabilities { get; init; } = SourceCapabilities.None;
    public string InstallHint { get; init; } = "";
}
