namespace UMango.Core.Security;

/// <summary>
/// 非 Windows 平台的密钥保护器：靠文件系统权限保护。
///
/// **强度说明（不要误以为它等价于 DPAPI）**：
/// 拿到这个文件的攻击者，若同时有当前用户的读写权限，就能解开它。
/// 它防的是「密钥明文躺在磁盘上被随手 grep 到」和「文件被拷走后随便谁都能读」，
/// 不防「同用户下的恶意进程」。
///
/// Windows 上不会用到这个（那里有 DPAPI），见 <c>WindowsDpapiKeyProtector</c>。
/// </summary>
public sealed class PortableKeyProtector : IKeyProtector
{
    /// <summary>单例。</summary>
    public static PortableKeyProtector Instance { get; } = new();

    /// <inheritdoc />
    public byte ProtectorVersion => 1;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rawKey) => rawKey.ToArray();

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedKey) => protectedKey.ToArray();
}
