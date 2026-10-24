namespace UMango.Core.Security;

/// <summary>
/// 密钥保护器：把「一把随机主密钥」保护成「一段能落盘的字节」。
///
/// 抽象出这个接口是因为不同平台的保护强度完全不同：
///   · Windows 用 DPAPI（用户凭据绑定，别的用户/别的机器解不开）；
///   · 其它平台没有等价物，只能靠文件系统权限 + 用户目录隔离。
///
/// **接口不承诺跨平台等价**。调用方（SecretService）只负责
/// 「用同一把保护器能解开自己写的东西」。
/// </summary>
public interface IKeyProtector
{
    /// <summary>保护原始密钥。</summary>
    byte[] Protect(ReadOnlySpan<byte> rawKey);

    /// <summary>还原原始密钥。</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedKey);

    /// <summary>当前平台的保护方式标识，写进密钥文件的版本字段里。</summary>
    byte ProtectorVersion { get; }
}
