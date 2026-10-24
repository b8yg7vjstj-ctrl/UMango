namespace UMango.Core.Security.Platform;

/// <summary>
/// 非 Windows 平台上的保护器选择：文件系统权限。
///
/// 只参与 net10.0 编译。
/// </summary>
public static class KeyProtectorFactory
{
    /// <summary>当前平台的保护器。</summary>
    public static IKeyProtector Default => NonWindowsKeyProtector.Instance;
}
