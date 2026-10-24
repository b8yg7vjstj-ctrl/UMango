namespace UMango.Core.Security.Platform;

/// <summary>
/// 非 Windows 平台的密钥保护器占位实现：退化为 <see cref="PortableKeyProtector"/>。
///
/// 只参与 net10.0 编译（见 csproj 的条件 ItemGroup）——
/// 这样 Core 的「跨平台可编译」那一支不会引用 Windows 专属的 DPAPI。
/// </summary>
public sealed class NonWindowsKeyProtector : IKeyProtector
{
    /// <summary>单例。</summary>
    public static NonWindowsKeyProtector Instance { get; } = new();

    /// <inheritdoc />
    public byte ProtectorVersion => PortableKeyProtector.Instance.ProtectorVersion;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rawKey) => PortableKeyProtector.Instance.Protect(rawKey);

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedKey) => PortableKeyProtector.Instance.Unprotect(protectedKey);
}
