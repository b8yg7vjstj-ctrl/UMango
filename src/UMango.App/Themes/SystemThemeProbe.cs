// SPDX-License-Identifier: Apache-2.0
//
// 本文件的探测方式参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Theme/SystemThemeHelper.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Theme → UMango.App.Themes；
//   2. **不用注册表** —— 注册表是 Windows 专属 API，而 UMango 要跨平台
//      （macOS/Linux）。这里改成按平台分派：
//        · Windows：读注册表 AppsUseLightTheme（与 PCL 同源、同键）；
//        · macOS：读 `defaults read -g AppleInterfaceStyle`（Dark）；
//        · Linux：读 `gsettings get org.gnome.desktop.interface color-scheme`
//          （'prefer-dark'）；
//   3. 探测结果统一成 Core 的 <see cref="SystemThemeState"/>，
//      纯逻辑（策略 → 深浅色）在 Core 里，方便在 macOS 上测试；
//   4. 任何异常都吞掉并返回「读不到」，不让主题探测失败影响应用启动。

using System;
using System.Diagnostics;
using UMango.Core.UI.Theme;
using UMango.Shell.Theming;

namespace UMango.App.Themes;

/// <summary>
/// 探测操作系统当前的深色/浅色设置。
/// </summary>
public static class SystemThemeProbe
{
    /// <summary>
    /// 把静态探测器适配成 Shell 编排层要的实例接口。
    ///
    /// <para>
    /// 静态类不能实现接口（CS0714），而 Shell 层的编排要一个可替换的实例
    /// （测试可以塞个假探测器）。这里用一个薄适配器把两者接上。
    /// </para>
    /// </summary>
    public static ISystemThemeProbe AsInterface { get; } = new ProbeAdapter();

    private sealed class ProbeAdapter : ISystemThemeProbe
    {
        public UMango.Core.UI.Theme.SystemThemeState Detect() => SystemThemeProbe.Detect();
    }

    /// <summary>探测系统主题。读不到时返回 <see cref="SystemThemeState.Unknown"/>。</summary>
    public static SystemThemeState Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DetectWindows();
            if (OperatingSystem.IsMacOS())
                return DetectMacOs();
            return DetectLinux();
        }
        catch (Exception)
        {
            return SystemThemeState.Unknown;
        }
    }

    private static SystemThemeState DetectWindows()
    {
        // 与 PCL-CE 同一个注册表键：AppsUseLightTheme = 0 表示深色。
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        if (key is null)
            return new SystemThemeState(false, false, "注册表键不存在");

        var value = key.GetValue("AppsUseLightTheme") as int?;
        if (value is null)
            return new SystemThemeState(false, false, "注册表值缺失或类型不符");

        return SystemThemeState.Known(value == 0);
    }

    private static SystemThemeState DetectMacOs()
    {
        // macOS 没有全局深色开关，只有应用级的 AppleInterfaceStyle。
        // 读不到通常意味着「没设置过」，也就是浅色。
        var output = RunCommand("defaults", "read", "-g", "AppleInterfaceStyle");
        if (output is null)
            return SystemThemeState.Known(false);

        return SystemThemeState.Known(output.Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase));
    }

    private static SystemThemeState DetectLinux()
    {
        var output = RunCommand("gsettings", "get", "org.gnome.desktop.interface", "color-scheme");
        if (output is null)
            return new SystemThemeState(false, false, "gsettings 不可用");

        var value = output.Trim();
        return SystemThemeState.Known(value.Equals("prefer-dark", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 执行一条命令并返回标准输出。命令不存在或失败时返回 null。
    /// </summary>
    private static string? RunCommand(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); } catch { /* 已经退了就无所谓 */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
