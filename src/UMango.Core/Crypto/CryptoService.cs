using System.Security.Cryptography;

namespace UMango.Core.Crypto;

/// <summary>
/// 加密服务：AES 加密/解密。
///
/// 对应 PCL-CE 的 Utils/Encryption/（5 文件，Apache-2.0）的通用部分。
/// PCL-CE 那边有 AES-CBC/GCM 和 ChaCha20-Poly1305；这里先实现 AES-GCM（AEAD，更安全）。
/// ChaCha20 需要 .NET 7+ 的 ChaCha20Poly1305，本项目用 .NET 10 可以用。
/// </summary>
public static class CryptoService
{
    /// <summary>AES-GCM 加密（带认证标签）。</summary>
    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) EncryptAesGcm(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[12]; // GCM 推荐 12 字节随机 nonce
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (ciphertext, nonce, tag);
    }

    /// <summary>AES-GCM 解密（验证认证标签）。</summary>
    public static byte[] DecryptAesGcm(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>ChaCha20-Poly1305 加密。</summary>
    public static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) EncryptChaCha20(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[12]; // 96-bit 随机 nonce
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, nonce, tag);
    }

    /// <summary>ChaCha20-Poly1305 解密。</summary>
    public static byte[] DecryptChaCha20(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>生成安全随机密钥。</summary>
    public static byte[] GenerateKey(int sizeBytes = 32)
    {
        var key = new byte[sizeBytes];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}
