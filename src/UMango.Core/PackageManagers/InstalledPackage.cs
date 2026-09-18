namespace UMango.Core.PackageManagers;

/// <summary>
/// 已安装的包。
///
/// 需求「应用列表读取参考 UnigetUI」的落点。
/// UnigetUI（现 Devolutions/UniGetUI，**MIT 许可**）的做法是：
/// 每个包管理器一个 bundle，各自实现「查找包 / 列出已安装」，
/// 再统一归一化成同一个模型展示给界面 —— 本项目的适配器模式与之同构，
/// 因此这里只是补齐「列出已安装」这一能力，不引入其代码。
/// </summary>
public sealed class InstalledPackage
{
    public required PackageSourceKind Source { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }

    /// <summary>该包自身的安装位置。**拿不到就留空，不要用管理器的根目录冒充**。</summary>
    public string? InstallPath { get; init; }

    /// <summary>
    /// 同一包额外安装的其他版本。
    /// Homebrew 的真实场景：<c>brew list --versions</c> 会输出 <c>brotli 1.1.0 1.2.0</c>。
    /// </summary>
    public IReadOnlyList<string> OtherVersions { get; init; } = [];
}
