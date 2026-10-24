// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/OS/SystemPaths.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.Utils.OS → UMango.Core.Utils.OS
//   · 原版 DriveLetter 用 Path.GetPathRoot(Environment.SystemDirectory)，
//     在非 Windows 上返回 "/"（根目录）而不是盘符 —— 名字有误导性。
//     这里改名为 RootPath 并补上 SystemDirectory / ProgramFiles 等常用路径，
//     按平台分派
// ----------------------------------------------------------------------------

namespace UMango.Core.Utils.OS;

/// <summary>系统常用路径。</summary>
public static class SystemPaths
{
    /// <summary>
    /// 文件系统的根路径。Windows 上是 "C:\"，macOS/Linux 上是 "/"。
    /// </summary>
    public static string RootPath =>
        Path.GetPathRoot(Environment.SystemDirectory) ?? Path.DirectorySeparatorChar.ToString();

    /// <summary>系统目录（Windows 上通常是 C:\Windows\System32）。</summary>
    public static string SystemDirectory => Environment.SystemDirectory;

    /// <summary>系统架构目录（Windows 上是 C:\Windows\System32 或 SysWOW64）。</summary>
    public static string SystemArchitectureDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>Program Files 目录（Windows 才有；其它平台返回空串）。</summary>
    public static string ProgramFiles =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : string.Empty;

    /// <summary>Program Files (x86) 目录（Windows 才有；其它平台返回空串）。</summary>
    public static string ProgramFilesX86 =>
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            : string.Empty;

    /// <summary>临时目录。</summary>
    public static string TempDirectory => Path.GetTempPath();

    /// <summary>用户主目录。</summary>
    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>应用数据目录（Windows 上 %APPDATA%，其它平台 ~/.config）。</summary>
    public static string AppDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>本机名。</summary>
    public static string MachineName => Environment.MachineName;

    /// <summary>当前用户域名或主机名。</summary>
    public static string UserDomainName => Environment.UserDomainName;
}
