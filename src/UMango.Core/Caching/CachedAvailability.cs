using UMango.Core.PackageManagers;

namespace UMango.Core.Caching;

/// <summary>
/// 一条持久化的包源可用性记录。
///
/// **为什么必须持久化**：探测一次可用性要起进程（版本探测 + 安装目录查询最多两次），
/// 六个包源就是最多 12 次进程启动。只放在内存里的话，**每次启动应用都要重来一遍** ——
/// 而可用性这种东西一天之内的变化概率极低。
///
/// 同时缓存**已解析的安装目录**：需求要求左栏分类的**子标题显示安装目录**，
/// 若只缓存"可用不可用"而不缓存目录，缓存命中时子标题就会退回平台默认值
/// （例如 npm 显示 /usr/local/lib/node_modules 而不是真实的 /opt/homebrew/lib/node_modules）。
/// </summary>
public sealed class CachedAvailability
{
    public required PackageSourceKind Kind { get; init; }
    public required AvailabilityState State { get; init; }
    public bool CliFound { get; init; }
    public string? CliPath { get; init; }
    public string? CliVersion { get; init; }
    public string? Detail { get; init; }

    /// <summary>探测时解析出的真实安装目录（用于补种适配器，避免子标题退回默认值）</summary>
    public string? ResolvedInstallDirectory { get; init; }

    /// <summary>该适配器的"未真机验证"标注（也要一起持久化，否则缓存命中后标注会丢）</summary>
    public string? VerificationNote { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    /// <summary>是否已超过有效期。</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan ttl) => now - CheckedAt > ttl;

    public SourceAvailability ToAvailability() => new()
    {
        Kind = Kind,
        State = State,
        CliFound = CliFound,
        CliPath = CliPath,
        CliVersion = CliVersion,
        Detail = Detail,
        CheckedAt = CheckedAt,
        VerificationNote = VerificationNote
    };
}
