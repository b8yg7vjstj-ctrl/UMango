// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/OS/SystemInfo.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.Utils.OS → UMango.Core.Utils.OS
//   · 与已有的 UMango.Core.Platform.SystemInfo 并存，故改名 SystemInfoEx，
//     避免两个同名类造成歧义（前者面向"包管理器能力门控"，
//     这里面向"诊断信息采集"）
//   · 原版 IsGBKEncoding 依赖 KernelInterop.GetACP()（Windows 专属 P/Invoke），
//     改为按平台分派：Windows 读当前 ANSI 代码页，macOS/Linux 恒为 false
//   · 原版 OSInfo 只拼 RuntimeInformation.OSDescription，
//     这里补上平台/架构/位数，便于诊断页直接展示
// ----------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace UMango.Core.Utils.OS;

/// <summary>
/// 系统信息（诊断用）。
///
/// 与 <see cref="UMango.Core.Platform.SystemInfo"/> 的分工：
/// 后者服务于**包管理器能力门控**（这个平台支不支持 winget），
/// 这里服务于**诊断信息采集**（这台机器是什么配置）。
/// </summary>
public static class SystemInfoEx
{
    /// <summary>是否为 32 位操作系统。</summary>
    public static bool Is32BitSystem => !Environment.Is64BitOperatingSystem;

    /// <summary>是否为 ARM64 架构。</summary>
    public static bool IsArm64System => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// 是否使用 GBK（简体中文 Windows 的默认 ANSI 代码页 936）编码。
    ///
    /// 原版直接调 Windows 专属的 <c>KernelInterop.GetACP()</c>；
    /// UMango 跨平台，这里改为按平台分派。
    /// </summary>
    public static bool IsGBKEncoding
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            return CurrentAnsiCodePage == 936;
        }
    }

    /// <summary>
    /// 当前进程的 ANSI 代码页（Windows 专属；其它平台返回 0）。
    ///
    /// 走 P/Invoke 拿 <c>GetACP()</c>：.NET 没有公开 API 读这个值，
    /// 而 <c>Encoding.Default</c> 在 .NET Core 上恒为 UTF-8，读不出真实代码页。
    /// </summary>
    public static int CurrentAnsiCodePage
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return 0;
            try
            {
                return GetAnsiCodePage();
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>系统信息描述，例如 "Microsoft Windows 11 专业工作站版 10.0.22635.0"。</summary>
    public static string OSInfo =>
        $"{RuntimeInformation.OSDescription} {Environment.OSVersion.Version}";

    /// <summary>
    /// 完整的诊断摘要：操作系统 + 架构 + 位数 + 运行时 + 处理器数。
    /// </summary>
    public static string Describe() =>
        $"{OSInfo}（{(Is32BitSystem ? "32 位" : "64 位")}，{RuntimeInformation.OSArchitecture}，" +
        $"运行时 {Environment.Version}，{Environment.ProcessorCount} 核）";

    // 用 DllImport 而不是 LibraryImport：LibraryImport 走源生成，
    // 需要类型为 partial 且工程开启 AllowUnsafeBlocks ——
    // 而 Core 的 net10.0 支是刻意不开启 unsafe 的（跨平台可编译的证明）。
    // 这里一个返回 int 的调用用 DllImport 完全够用。
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetACP")]
    private static extern int GetAnsiCodePage();
}
