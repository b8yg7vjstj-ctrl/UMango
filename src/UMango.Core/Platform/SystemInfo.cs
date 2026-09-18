using System.Runtime.InteropServices;

namespace UMango.Core.Platform;

/// <summary>
/// 系统判断。需求「新写系统判断」的落点：
/// 平台、架构、家目录、以及各包管理器的默认安装目录都在这里集中判定，
/// 界面侧不再各自 if-else 判断系统。
/// </summary>
public static class SystemInfo
{
    /// <summary>当前平台</summary>
    public static PlatformKind Platform
    {
        get
        {
            if (OperatingSystem.IsWindows()) return PlatformKind.Windows;
            if (OperatingSystem.IsMacOS()) return PlatformKind.MacOs;
            return PlatformKind.Linux;
        }
    }

    /// <summary>当前架构（x64 / arm64）</summary>
    public static string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    };

    /// <summary>是否 Windows（窗口三大金刚键用系统原生风格的判断依据）</summary>
    public static bool IsWindows => Platform == PlatformKind.Windows;

    /// <summary>用户主目录</summary>
    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>平台信息摘要（诊断页展示）</summary>
    public static string Describe() => $"{Platform.DisplayName()} {Architecture}";
}
