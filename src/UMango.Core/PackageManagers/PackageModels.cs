using UMango.Core.Platform;

namespace UMango.Core.PackageManagers;

/// <summary>包源种类。需求里要管的六种包管理器。</summary>
public enum PackageSourceKind
{
    Npm, Pnpm, Git, Scoop, Winget, Homebrew
}

public static class PackageSourceKindExtensions
{
    /// <summary>稳定标识（配置/日志/记录用，改动即破坏兼容）</summary>
    public static string Id(this PackageSourceKind k) => k switch
    {
        PackageSourceKind.Npm => "npm",
        PackageSourceKind.Pnpm => "pnpm",
        PackageSourceKind.Git => "git",
        PackageSourceKind.Scoop => "scoop",
        PackageSourceKind.Winget => "winget",
        _ => "homebrew"
    };

    /// <summary>**界面上作为分类标题显示的名字**</summary>
    public static string DisplayName(this PackageSourceKind k) => k switch
    {
        PackageSourceKind.Npm => "npm",
        PackageSourceKind.Pnpm => "pnpm",
        PackageSourceKind.Git => "Git",
        PackageSourceKind.Scoop => "Scoop",
        PackageSourceKind.Winget => "WinGet",
        _ => "Homebrew"
    };

    /// <summary>平台支持。UI 据此隐藏当前系统不支持的包源。</summary>
    public static IReadOnlyList<PlatformKind> SupportedPlatforms(this PackageSourceKind k) => k switch
    {
        PackageSourceKind.Npm or PackageSourceKind.Pnpm or PackageSourceKind.Git =>
            [PlatformKind.Windows, PlatformKind.MacOs, PlatformKind.Linux],
        PackageSourceKind.Winget or PackageSourceKind.Scoop => [PlatformKind.Windows],
        _ => [PlatformKind.MacOs, PlatformKind.Linux],   // Homebrew 非 Windows
    };

    public static bool Supports(this PackageSourceKind k, PlatformKind p) =>
        k.SupportedPlatforms().Contains(p);
}

/// <summary>
/// 下载方式。**这是「按不同下载方式列明子项进度」的分类依据**：
/// 同一次安装会拆成若干步骤，每步的下载方式不同，界面上各占一行进度。
/// </summary>
public enum DownloadMethod
{
    /// <summary>包管理器注册表（npm/pnpm 的 registry 元数据）</summary>
    RegistryMetadata,
    /// <summary>注册表 tarball 直下</summary>
    RegistryTarball,
    /// <summary>Git 克隆</summary>
    GitClone,
    /// <summary>Git 子模块/检出</summary>
    GitCheckout,
    /// <summary>bucket / manifest 拉取（scoop、winget）</summary>
    ManifestFetch,
    /// <summary>直链下载（scoop 的 url、winget installer）</summary>
    DirectUrl,
    /// <summary>Homebrew bottle 下载</summary>
    BottleDownload,
    /// <summary>源码编译</summary>
    SourceBuild,
    /// <summary>依赖解析</summary>
    DependencyResolve,
    /// <summary>链接/写入安装（npm link、scoop shim、brew link）</summary>
    LinkWrite,
    /// <summary>多协议下载器（aria2）</summary>
    Aria2,
    /// <summary>BitTorrent</summary>
    BitTorrent,
    /// <summary>eD2k</summary>
    Ed2k,
    /// <summary>未知/其他</summary>
    Unknown
}

public static class DownloadMethodExtensions
{
    /// <summary>界面显示名（中文，与子项进度同级显示）</summary>
    public static string DisplayName(this DownloadMethod m) => m switch
    {
        DownloadMethod.RegistryMetadata => "读取注册表元数据",
        DownloadMethod.RegistryTarball => "下载注册表压缩包",
        DownloadMethod.GitClone => "Git 克隆",
        DownloadMethod.GitCheckout => "Git 检出",
        DownloadMethod.ManifestFetch => "拉取清单",
        DownloadMethod.DirectUrl => "直链下载",
        DownloadMethod.BottleDownload => "下载 Bottle",
        DownloadMethod.SourceBuild => "源码编译",
        DownloadMethod.DependencyResolve => "解析依赖",
        DownloadMethod.LinkWrite => "链接与写入",
        DownloadMethod.Aria2 => "aria2 多线程下载",
        DownloadMethod.BitTorrent => "BitTorrent 下载",
        DownloadMethod.Ed2k => "eD2k 下载",
        _ => "其他"
    };
}

/// <summary>安装步骤。每个步骤在进度界面各占一行子项。</summary>
public sealed class InstallStep
{
    public required string Title { get; init; }
    public required DownloadMethod Method { get; init; }
    /// <summary>该步骤在总进度中的权重（用于父进度条加权汇总）</summary>
    public double Weight { get; init; } = 1.0;
}

/// <summary>包源能力声明。UI 据此决定按钮是否可用。</summary>
public sealed record SourceCapabilities(
    bool Search,
    bool ListVersions,
    bool ResolveDependencies,
    bool OfficialDownloadCounts,
    bool Install,
    bool Uninstall,
    bool Upgrade)
{
    /// <summary>
    /// 是否支持列出已安装的包。
    /// 用 init-only 属性而不是构造参数：既向后兼容现有的 6 处 new(...) 位置参数调用，
    /// 又让个别适配器可以单独关掉这个能力。
    /// </summary>
    public bool ListInstalled { get; init; } = true;

    public static readonly SourceCapabilities None = new(false, false, false, false, false, false, false);
}

/// <summary>包源可用性状态</summary>
public enum AvailabilityState
{
    Available, Degraded, UnsupportedPlatform, NotInstalled, Unreachable
}

/// <summary>包源可用性探测结果</summary>
public sealed class SourceAvailability
{
    public required PackageSourceKind Kind { get; init; }
    public required AvailabilityState State { get; init; }
    public bool CliFound { get; init; }
    public string? CliPath { get; init; }
    public string? CliVersion { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public string? Detail { get; init; }

    /// <summary>该适配器的解析逻辑是否已在真机验证过（未验证的如实告知用户）</summary>
    public string? VerificationNote { get; init; }

    public bool IsUsable => State is AvailabilityState.Available or AvailabilityState.Degraded;
}

/// <summary>包信息（列表卡片）</summary>
public sealed class PackageInfo
{
    public required PackageSourceKind Source { get; init; }
    public required string Name { get; init; }
    public string? Summary { get; init; }
    public string? LatestVersion { get; init; }
    public long? DownloadCount { get; init; }
    /// <summary>下载量的来源说明（官方 / GitHub Stars 兜底）</summary>
    public string? DownloadCountSource { get; init; }
    public bool DownloadCountIsFallback { get; init; }
    public string? License { get; init; }
    public string? Homepage { get; init; }
}

/// <summary>包版本</summary>
public sealed class PackageVersion
{
    public required string Version { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public bool Deprecated { get; init; }
    public string? Note { get; init; }
}

/// <summary>依赖声明（未解析）</summary>
public sealed record DependencyRef(string Name, string Requirement, bool IsDev = false);
