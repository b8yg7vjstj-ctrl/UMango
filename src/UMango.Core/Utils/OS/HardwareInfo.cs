// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/OS/HardwareInfo.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 采用时做的改动（较多，因为原版完全依赖 Windows 专属的 WMI 与 P/Invoke）：
//   1. 命名空间 PCL.Core.Utils.OS → UMango.Core.Utils.OS
//   2. **CPU / GPU 名称改为跨平台获取**：
//      原版用 System.Management（WMI，Windows 专属）查 Win32_Processor /
//      Win32_VideoController。UMango 跨平台，改为：
//        · CPU：优先读 /proc/cpuinfo（Linux）或 sysctl（macOS），
//          读不到就退回 RuntimeInformation.ProcessArchitecture 的中性描述；
//        · GPU：不猜 —— 跨平台没有可靠又轻量的统一接口，
//          宁可留空也不要给出错误的型号。
//   3. **物理内存改为 .NET 自带 API**：GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
//      （原版走 KernelInterop.GetPhysicalMemoryBytes 的 Windows P/Invoke）
//   4. 处理器核心数用 Environment.ProcessorCount（原版没提供）
//   5. 原版把结果存进 public static 字段供外部读取；
//      这里改成不可变的快照记录 + 一个显式的 Collect() 方法，
//      避免"静态可变全局状态"在多线程下读到半初始化值
// ----------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace UMango.Core.Utils.OS;

/// <summary>硬件信息快照。</summary>
public sealed record HardwareSnapshot
{
    /// <summary>CPU 型号（读不到时为中性描述）。</summary>
    public string CpuName { get; init; } = "Unknown";

    /// <summary>逻辑处理器数。</summary>
    public int ProcessorCount { get; init; }

    /// <summary>已安装物理内存（字节）。</summary>
    public long PhysicalMemoryBytes { get; init; }

    /// <summary>已安装物理内存（MiB）。</summary>
    public long PhysicalMemoryMiB => PhysicalMemoryBytes / 1024 / 1024;
}

/// <summary>系统硬件信息采集。</summary>
public static class HardwareInfo
{
    private static readonly object Lock = new();
    private static HardwareSnapshot? _current;

    /// <summary>最近一次采集到的硬件信息（未采集过则现采一次）。</summary>
    public static HardwareSnapshot Current
    {
        get
        {
            var snapshot = _current;
            if (snapshot is not null)
                return snapshot;
            return Collect();
        }
    }

    /// <summary>
    /// 采集硬件信息。CPU 型号读不到时退回中性描述，绝不抛异常 ——
    /// 诊断信息是锦上添花，取不到不该影响主流程。
    /// </summary>
    public static HardwareSnapshot Collect()
    {
        var snapshot = new HardwareSnapshot
        {
            CpuName = ReadCpuName(),
            ProcessorCount = Environment.ProcessorCount,
            PhysicalMemoryBytes = ReadPhysicalMemoryBytes(),
        };

        lock (Lock)
            _current = snapshot;
        return snapshot;
    }

    /// <summary>CPU 型号。</summary>
    public static string ReadCpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return ReadLinuxCpuName();
            if (OperatingSystem.IsMacOS())
                return ReadMacCpuName();
        }
        catch
        {
            // 读不到就走下面的中性描述。
        }

        return FallbackCpuName;
    }

    /// <summary>
    /// 读不到 CPU 型号时的中性描述。
    ///
    /// 单独暴露出来是为了能被测到：在真实机器上探测几乎总是成功，
    /// 退化分支永远走不到，测试就咬不住它。
    /// </summary>
    public static string FallbackCpuName => $"{RuntimeInformation.ProcessArchitecture} 处理器";

    private static string ReadLinuxCpuName()
    {
        const string path = "/proc/cpuinfo";
        if (!File.Exists(path))
            return "Unknown";

        foreach (var line in File.ReadLines(path))
        {
            // 形如 "model name : Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz"
            if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                continue;
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var name = line[(colon + 1)..].Trim();
            return name.Length > 0 ? name : "Unknown";
        }

        return "Unknown";
    }

    private static string ReadMacCpuName()
    {
        // macOS 用 sysctl 读 machdep.cpu.brand_string。
        var startInfo = new System.Diagnostics.ProcessStartInfo("sysctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("machdep.cpu.brand_string");

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
            return "Unknown";

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(2000))
        {
            try { process.Kill(); } catch { /* 已经退了就无所谓 */ }
            return "Unknown";
        }

        var name = output.Trim();
        return name.Length > 0 ? name : "Unknown";
    }

    /// <summary>已安装物理内存（字节）。</summary>
    public static long ReadPhysicalMemoryBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
                return info.TotalAvailableMemoryBytes;
        }
        catch
        {
            // 某些运行时上取不到，往下走。
        }

        try
        {
            // 退化路径：用进程的工作集当不了物理内存，
            // 但至少给一个非零的下限，避免界面显示 "0 MiB"。
            return Environment.WorkingSet;
        }
        catch
        {
            return 0;
        }
    }
}
