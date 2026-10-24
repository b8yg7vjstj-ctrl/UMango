// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Encryption/ChaCha20Poly1305Provider.cs 逐字采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.Utils.Encryption → UMango.Core.Security；
//   2. 类改 sealed（没有派生需求）；
//   3. HKDF 的 info 串由 "PCL.Core.Utils.Encryption.ChaCha20" 改为
//      "UMango.Core.Security.ChaCha20" —— 派生密钥的上下文绑定串，
//      换名字意味着 UMango 派生的密钥与 PCL 派生的密钥不同，
//      这正是我们想要的（UMango 不与 PCL 共用密钥材料）。

using System.Security.Cryptography;

namespace UMango.Core.Security;

/// <summary>
/// ChaCha20-Poly1305。硬件不支持 AES-GCM 时的首选（有纯软件实现兜底）。
/// </summary>
public sealed class ChaCha20Poly1305Provider : IEncryptionProvider
{
    /// <summary>单例。</summary>
    public static ChaCha20Poly1305Provider Instance { get; } = new();

    private const int NonceSize = 12;    // 96-bit nonce for ChaCha20Poly1305
    private const int TagSize = 16;      // 128-bit authentication tag
    private const int KeySize = 32;      // 256-bit key
    private const int SaltSize = 16;     // 128-bit salt for HKDF

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        // Generate random salt, nonce and the tag
        var salt = new byte[SaltSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        RandomNumberGenerator.Fill(tag);

        // Derive key using the salt
        Span<byte> outputKey = stackalloc byte[KeySize];
        _DeriveKey(key, salt, outputKey);
        using var chacha = new ChaCha20Poly1305(outputKey);

        // Prepare output arrays
        var ciphertext = new byte[data.Length];

        // Perform encryption
        chacha.Encrypt(nonce, data, ciphertext, tag);

        // Make the encryption data: salt + nonce + tag + ciphertext
        var result = new byte[SaltSize + NonceSize + ciphertext.Length + TagSize];
        var resultSpan = result.AsSpan();

        salt.CopyTo(resultSpan[..SaltSize]);
        nonce.CopyTo(resultSpan.Slice(SaltSize, NonceSize));
        tag.CopyTo(resultSpan.Slice(SaltSize + NonceSize, TagSize));
        ciphertext.CopyTo(resultSpan.Slice(SaltSize + NonceSize + TagSize, ciphertext.Length));

        return result;
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        // Verify minimum data length
        if (data.Length < SaltSize + NonceSize + TagSize)
            throw new ArgumentException("Invalid encrypted data length");

        // Encryption data: salt + nonce + tag + ciphertext
        var salt = data[..SaltSize];
        var nonce = data.Slice(SaltSize, NonceSize);
        var tag = data.Slice(SaltSize + NonceSize, TagSize);
        var ciphertext = data[(SaltSize + NonceSize + TagSize)..];

        // Derive key using the extracted salt
        Span<byte> outputKey = stackalloc byte[KeySize];
        _DeriveKey(key, salt, outputKey);
        using var chacha = new ChaCha20Poly1305(outputKey);

        // Perform decryption
        var plaintext = new byte[ciphertext.Length];
        chacha.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private static readonly byte[] _Info = "UMango.Core.Security.ChaCha20"u8.ToArray();

    private static void _DeriveKey(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, Span<byte> outputKey)
    {
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            outputKey,
            salt,
            _Info.AsSpan());
    }

    /// <inheritdoc />
    public bool IsSupported => ChaCha20Poly1305.IsSupported;
}
