using UMango.Core.Platform;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 包管理器注册表：集中处理**平台门控**与调度。
/// 注册表注册全部适配器（不做注册期过滤），由 SupportedPlatforms 决定谁可用 ——
/// 这样界面才能表达"该包源在当前系统不可用"，而不是让它凭空消失。
/// </summary>
public sealed class PackageManagerRegistry
{
    private readonly List<IPackageManager> _managers;
    private readonly Dictionary<PackageSourceKind, SourceAvailability> _cache = [];

    public PackageManagerRegistry(IEnumerable<IPackageManager> managers)
        => _managers = managers.ToList();

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
        if (!refresh && _cache.TryGetValue(kind, out var cached)) return cached;

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

        SourceAvailability result;
        if (!kind.Supports(SystemInfo.Platform))
        {
            // 平台不支持：不执行 CLI，直接门控。
            // 注意仍要带上适配器自报的 VerificationNote —— 被门控的包源（macOS 上的 winget / scoop）
            // 恰恰也是"未在真机验证"的那些，门控路径若丢掉标注，界面就不会显示"未验证"提示。
            result = new SourceAvailability
            {
                Kind = kind,
                State = AvailabilityState.UnsupportedPlatform,
                Detail = $"{kind.DisplayName()} 仅在 {string.Join(" / ", kind.SupportedPlatforms().Select(p => p.DisplayName()))} 上可用",
                VerificationNote = m.VerificationNote
            };
        }
        else
        {
            result = await m.ProbeAsync(ct).ConfigureAwait(false);
        }

        _cache[kind] = result;
        return result;
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
