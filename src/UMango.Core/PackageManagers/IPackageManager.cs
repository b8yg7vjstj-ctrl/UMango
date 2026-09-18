using UMango.Core.Platform;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 包管理器适配器契约。
///
/// 设计要点（对应需求）：
///   · <see cref="DisplayName"/> 是**分类标题**（界面上以包管理器名作为分类）；
///   · <see cref="InstallDirectory"/> 是**子标题**（显示该管理器的安装目录）；
///   · <see cref="PlanInstallAsync"/> 返回按**下载方式**拆分的步骤列表，
///     界面据此为每个子项单独显示进度；
///   · 所有方法都接受 CancellationToken，配合流式进程运行器实现可观测输出。
/// </summary>
public interface IPackageManager
{
    PackageSourceKind Kind { get; }

    /// <summary>分类标题（= 包管理器名）</summary>
    string DisplayName { get; }

    /// <summary>子标题（= 该包管理器的安装目录，界面第二行显示）</summary>
    string InstallDirectory { get; }

    /// <summary>依赖的 CLI 可执行名</summary>
    string CliName { get; }

    IReadOnlyList<PlatformKind> SupportedPlatforms { get; }

    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// 该适配器的解析逻辑是否已在真机验证过；未验证时给出**如实的说明文案**（界面显示"未验证"提示），
    /// null = 已验证、无需提示。
    ///
    /// 为什么它必须出现在接口上：注册表的平台门控**不会调用 <see cref="ProbeAsync"/>**
    /// （门控必须保证不执行任何 CLI），而 VerificationNote 原先只能从 ProbeAsync 的返回值里取 ——
    /// 结果是在当前系统不可用的包源（macOS 上的 winget / scoop）会丢掉"未验证"标注。
    /// 因此这个信息必须能独立于探测拿到。
    /// </summary>
    string? VerificationNote { get; }

    /// <summary>未安装时给用户的安装指引</summary>
    string InstallHint { get; }

    /// <summary>探测可用性（是否装了 CLI、能否应答）</summary>
    Task<SourceAvailability> ProbeAsync(CancellationToken ct = default);

    /// <summary>搜索包</summary>
    Task<IReadOnlyList<PackageInfo>> SearchAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>
    /// 列出**已安装**的包（需求「应用列表读取」）。
    /// 不支持的适配器由基类返回"不支持"错误，界面据此隐藏入口。
    /// </summary>
    Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct = default);

    /// <summary>列出全部版本</summary>
    Task<IReadOnlyList<PackageVersion>> GetVersionsAsync(string name, CancellationToken ct = default);

    /// <summary>直接依赖声明</summary>
    Task<IReadOnlyList<DependencyRef>> GetDependenciesAsync(
        string name, string? version, CancellationToken ct = default);

    /// <summary>
    /// 规划安装步骤：按下载方式拆成若干子项，供进度界面逐项显示。
    /// 只做规划，不执行（执行由后续的安装器按步骤驱动）。
    /// </summary>
    Task<IReadOnlyList<InstallStep>> PlanInstallAsync(
        string name, string? version, CancellationToken ct = default);
}
