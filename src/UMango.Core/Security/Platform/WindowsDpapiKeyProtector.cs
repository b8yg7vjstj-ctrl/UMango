namespace UMango.Core.Security.Platform;

/// <summary>
/// Windows 上的密钥保护器：DPAPI（<c>ProtectedData</c>，CurrentUser 作用域）。
///
/// 保护后的密钥只有**同一个 Windows 用户**在同一台机器上能解开 ——
/// 换用户、换机器、拷到别的系统都解不开。
///
/// 只参与 net10.0-windows 编译（见 csproj 的条件 ItemGroup）。
/// </summary>
public sealed class WindowsDpapiKeyProtector : IKeyProtector
{
    /// <summary>单例。</summary>
    public static WindowsDpapiKeyProtector Instance { get; } = new();

    /// <summary>
    /// DPAPI 的可选熵（additional entropy）。
    ///
    /// 它的作用不是「加密」而是「绑定用途」：即便有人拿到了同一用户下的
    /// 某段 DPAPI 密文，也不能把它当成 UMango 的主密钥来用。
    /// </summary>
    private static readonly byte[] _Entropy = "UMango.SecretService.Key"u8.ToArray();

    /// <inheritdoc />
    public byte ProtectorVersion => 2;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rawKey)
    {
        EnsureSupported();
        return System.Security.Cryptography.ProtectedData.Protect(
            rawKey.ToArray(), _Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedKey)
    {
        EnsureSupported();
        return System.Security.Cryptography.ProtectedData.Unprotect(
            protectedKey.ToArray(), _Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// DPAPI 只存在于 Windows。
    ///
    /// `net10.0-windows` 是目标框架标记，不保证运行在 Windows 上
    /// （UMango 的测试工程就是 `net10.0-windows` 但跑在 macOS 主机上）。
    /// 缺这道检查时，调用方会拿到一个难以理解的
    /// <see cref="PlatformNotSupportedException"/>。
    /// </summary>
    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用。");
    }
}
