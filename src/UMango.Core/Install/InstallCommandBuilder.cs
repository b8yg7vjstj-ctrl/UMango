using UMango.Core.PackageManagers;

namespace UMango.Core.Install;

/// <summary>
/// 构建各包管理器的安装/卸载命令。
///
/// 单独抽出来的原因：命令拼装与「进度、回传、记录」是正交的关注点，
/// 混在适配器里会让每个适配器都要重复实现一遍日志与进度接线（降低重复代码率）。
/// </summary>
public static class InstallCommandBuilder
{
    /// <summary>构建安装命令。global 表示是否全局安装（对支持该概念的包管理器生效）。</summary>
    public static (string Program, List<string> Args, string Explanation) BuildInstall(
        PackageSourceKind kind, string name, string? version, bool global)
    {
        var target = string.IsNullOrWhiteSpace(version) ? name : $"{name}@{version}";
        return kind switch
        {
            PackageSourceKind.Npm => global
                ? ("npm", ["install", "-g", target, "--no-fund", "--no-audit"], $"全局安装 {target}（npm）")
                : ("npm", ["install", target, "--no-fund", "--no-audit"], $"在当前目录安装 {target}（npm）"),

            PackageSourceKind.Pnpm => global
                ? ("pnpm", ["add", "-g", target], $"全局安装 {target}（pnpm）")
                : ("pnpm", ["add", target], $"在当前目录安装 {target}（pnpm）"),

            // git：克隆到安装目录（版本用分支/tag 检出）
            PackageSourceKind.Git => ("git",
                string.IsNullOrWhiteSpace(version)
                    ? ["clone", "--progress", NormalizeGitUrl(name)]
                    : ["clone", "--progress", "--branch", version, NormalizeGitUrl(name)],
                $"克隆 {name}{(string.IsNullOrWhiteSpace(version) ? "" : $"（{version}）")}"),

            PackageSourceKind.Scoop => ("scoop",
                string.IsNullOrWhiteSpace(version)
                    ? ["install", name]
                    : ["install", $"{name}@{version}"],
                $"安装 {name}（Scoop）"),

            PackageSourceKind.Winget => ("winget",
                ["install", "--id", name, "--exact", "--accept-package-agreements",
                 "--accept-source-agreements", "--disable-interactivity"],
                $"安装 {name}（WinGet）"),

            PackageSourceKind.Homebrew => ("brew",
                string.IsNullOrWhiteSpace(version)
                    ? ["install", name]
                    : ["install", $"{name}@{version}"],
                $"安装 {name}（Homebrew）"),

            _ => (kind.Id(), [name], $"安装 {name}")
        };
    }

    /// <summary>构建卸载命令。</summary>
    public static (string Program, List<string> Args, string Explanation) BuildUninstall(
        PackageSourceKind kind, string name, bool global)
    {
        return kind switch
        {
            PackageSourceKind.Npm => global
                ? ("npm", ["uninstall", "-g", name], $"全局卸载 {name}（npm）")
                : ("npm", ["uninstall", name], $"卸载 {name}（npm）"),
            PackageSourceKind.Pnpm => global
                ? ("pnpm", ["remove", "-g", name], $"全局卸载 {name}（pnpm）")
                : ("pnpm", ["remove", name], $"卸载 {name}（pnpm）"),
            PackageSourceKind.Scoop => ("scoop", ["uninstall", name], $"卸载 {name}（Scoop）"),
            PackageSourceKind.Winget => ("winget",
                ["uninstall", "--id", name, "--exact", "--disable-interactivity"], $"卸载 {name}（WinGet）"),
            PackageSourceKind.Homebrew => ("brew", ["uninstall", name], $"卸载 {name}（Homebrew）"),
            // git 没有"卸载"概念，只有删除本地目录
            _ => ("git", ["--version"], $"{name}（git 仓库需手动删除目录）")
        };
    }

    /// <summary>owner/repo 简写补全为可克隆 URL。</summary>
    public static string NormalizeGitUrl(string name)
    {
        if (name.StartsWith("http://") || name.StartsWith("https://") ||
            name.StartsWith("git://") || name.StartsWith("git@") || name.StartsWith("ssh://"))
            return name;
        return $"https://github.com/{name.Trim('/')}";
    }
}
