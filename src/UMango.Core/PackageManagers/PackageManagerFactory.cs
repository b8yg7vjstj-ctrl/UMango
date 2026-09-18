using UMango.Core.PackageManagers.Adapters;

namespace UMango.Core.PackageManagers;

/// <summary>
/// 默认注册表工厂：把六个包管理器适配器**全部**注册进去。
///
/// 设计要点（与 <see cref="PackageManagerRegistry"/> 的约定一致）：
///   · 这里**不做平台过滤**。winget / scoop / homebrew 在不适用的平台上同样注册，
///     由注册表按 <see cref="IPackageManager.SupportedPlatforms"/> 门控。
///     这样界面才能显示"该包源在当前系统不可用"，而不是让它凭空消失。
///   · 顺序即界面左栏分类的显示顺序：先跨平台的（npm / pnpm / git），
///     再 Windows（winget / scoop），最后 macOS/Linux（homebrew）。
/// </summary>
public static class PackageManagerFactory
{
    /// <summary>创建注册了全部六个适配器的默认注册表。</summary>
    public static PackageManagerRegistry CreateDefault() => new(
    [
        new NpmPackageManager(),
        new PnpmPackageManager(),
        new GitPackageManager(),
        new WingetPackageManager(),
        new ScoopPackageManager(),
        new HomebrewPackageManager(),
    ]);
}
