// SPDX-License-Identifier: Apache-2.0
//
// 本文件的系统代理读取方式参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/IO/Net/Http/HttpProxyManager.cs（Apache-2.0）。
//
// 采用时做的改动：
//   1. 原版在 Core 里直接用 `Microsoft.Win32.Registry` 读 Windows 的系统代理 ——
//      那让 Core 只能在 Windows 上编译。UMango 的 Core 必须在 net10.0 支编译
//      （跨平台性由编译期证明），所以平台相关的读取放在 App 层，
//      Core 只保留 `ISystemProxyProbe` 接口与代理选择逻辑；
//   2. 原版用 `RegistryChangeMonitor` 监听注册表变化（Windows 专属），
//      这里改为按需读取 + 用户手动刷新；
//   3. Windows 读的键与原版完全一致：
//      HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings
//      的 ProxyEnable 与 ProxyServer。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UMango.Core.Network;

namespace UMango.App.Network;

/// <summary>
/// 按平台读取系统代理设置。
///
/// · Windows：读注册表 ProxyEnable / ProxyServer（与 PCL 同键同值）；
/// · macOS：读 `scutil --proxy` 的输出里的 <c>HTTPProxy : host:port</c> 与
///   <c>HTTPEnable : 1</c>；
/// · Linux：读 <c>gsettings get org.gnome.system.proxy mode</c> 与
///   <c>org.gnome.system.proxy.http host/port</c>。
/// </summary>
public sealed class PlatformSystemProxyProbe : ISystemProxyProbe
{
    /// <inheritdoc />
    public bool TryRead(out bool enabled, out string? proxyString)
    {
        enabled = false;
        proxyString = null;

        try
        {
            if (OperatingSystem.IsWindows())
                return TryReadWindows(out enabled, out proxyString);
            if (OperatingSystem.IsMacOS())
                return TryReadMacOs(out enabled, out proxyString);
            return TryReadLinux(out enabled, out proxyString);
        }
        catch
        {
            // 读不到就当作「没有系统代理」，不能让网络层因此崩掉。
            enabled = false;
            proxyString = null;
            return false;
        }
    }

    private static bool TryReadWindows(out bool enabled, out string? proxyString)
    {
        enabled = false;
        proxyString = null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
            return false;

        enabled = (key.GetValue("ProxyEnable") as int? ?? 0) != 0;
        proxyString = key.GetValue("ProxyServer") as string;
        return true;
    }

    private static bool TryReadMacOs(out bool enabled, out string? proxyString)
    {
        enabled = false;
        proxyString = null;
        var output = RunAndCapture("scutil", "--proxy");
        if (output is null)
            return false;

        // scutil --proxy 的输出形如：
        //   <dictionary> { HTTPEnable : 1 ... HTTPProxy : 127.0.0.1:7890 ... }
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("HTTPEnable", StringComparison.Ordinal))
            {
                enabled = trimmed.Contains(": 1", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("HTTPProxy", StringComparison.Ordinal))
            {
                var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0)
                {
                    var value = trimmed[(colon + 1)..].Trim();
                    if (value.Length > 0)
                        proxyString = value;
                }
            }
        }
        return true;
    }

    private static bool TryReadLinux(out bool enabled, out string? proxyString)
    {
        enabled = false;
        proxyString = null;
        var mode = RunAndCapture("gsettings", "get", "org.gnome.system.proxy", "mode");
        if (mode is null)
            return false;

        enabled = mode.Contains("'manual'", StringComparison.Ordinal);
        if (!enabled)
            return true;

        var host = RunAndCapture("gsettings", "get", "org.gnome.system.proxy.http", "host");
        var port = RunAndCapture("gsettings", "get", "org.gnome.system.proxy.http", "port");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port))
        {
            proxyString = null;
            return true;
        }

        proxyString = $"{host.Trim().Trim('\'')}:{port.Trim().Trim('\'')}";
        return true;
    }

    /// <summary>跑一个外部命令并取标准输出。命令不存在时返回 null。</summary>
    private static string? RunAndCapture(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            // 不等退出 —— 这些探测命令都是秒回的，但没必要为此卡住主线程。
            return output;
        }
        catch
        {
            return null;
        }
    }
}
