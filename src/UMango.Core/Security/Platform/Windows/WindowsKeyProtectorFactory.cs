namespace UMango.Core.Security.Platform;

/// <summary>
/// Windows 上的保护器选择：DPAPI。
///
/// 只参与 net10.0-windows 编译。
///
/// **为什么还要在运行时再判一次平台**：
/// `net10.0-windows` 只是一个**目标框架标记**，不代表代码一定跑在 Windows 上 ——
/// UMango 的测试工程正是 `net10.0-windows`（为了能引用 WPF 侧的类型），
/// 却要在 macOS 主机上跑。DPAPI 在非 Windows 上调用会抛
/// <see cref="PlatformNotSupportedException"/>，所以这里必须用
/// <see cref="OperatingSystem.IsWindows"/> 在运行时选路。
/// </summary>
public static class KeyProtectorFactory
{
    /// <summary>当前平台的保护器。</summary>
    public static IKeyProtector Default => OperatingSystem.IsWindows()
        ? WindowsDpapiKeyProtector.Instance
        : PortableKeyProtector.Instance;
}
