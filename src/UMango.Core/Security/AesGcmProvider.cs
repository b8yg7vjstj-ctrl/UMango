// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/Utils/Encryption/AesGcmProvider.cs 逐字采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.Utils.Encryption → UMango.Core.Security；
//   2. 命名空间从块语法改为文件级语法（风格统一，内容不变）；
//   3. 类改 sealed（没有派生需求）。

using System.Security.Cryptography;

namespace UMango.Core.Security;

/// <summary>
/// AES-GCM。优先选用：Windows 10+ 与主流 CPU 都有硬件加速。
/// </summary>
public sealed class AesGcmProvider : IEncryptionProvider
{
    /// <summary>单例。</summary>
    public static AesGcmProvider Instance { get; } = new();

    private const int NonceSize = 12; // 96 bits
    private const int TagSize = 16;   // 128 bits

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[data.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, data, ciphertext, tag);
        }

        // [Nonce(12)] [Tag(16)] [Ciphertext(n)]
        byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return result;
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (data.Length < NonceSize + TagSize)
            throw new ArgumentException("加密数据长度不足。");

        ReadOnlySpan<byte> nonce = data.Slice(0, NonceSize);
        ReadOnlySpan<byte> tag = data.Slice(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = data.Slice(NonceSize + TagSize);

        byte[] plaintext = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    /// <inheritdoc />
    public bool IsSupported => AesGcm.IsSupported;
}
