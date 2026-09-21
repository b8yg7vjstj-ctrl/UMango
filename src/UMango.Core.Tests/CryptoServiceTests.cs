using System.Security.Cryptography;
using UMango.Core.Crypto;
using Xunit;

namespace UMango.Core.Tests;

public class CryptoServiceTests
{
    [Fact]
    public void AESGCM加密解密往返()
    {
        var key = CryptoService.GenerateKey(32);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello world");
        var (ciphertext, nonce, tag) = CryptoService.EncryptAesGcm(plaintext, key);
        var decrypted = CryptoService.DecryptAesGcm(ciphertext, key, nonce, tag);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AESGCM_篡改密文后解密失败()
    {
        var key = CryptoService.GenerateKey(32);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret");
        var (ciphertext, nonce, tag) = CryptoService.EncryptAesGcm(plaintext, key);
        ciphertext[0] ^= 0xFF; // 篡改
        Assert.ThrowsAny<CryptographicException>(() => CryptoService.DecryptAesGcm(ciphertext, key, nonce, tag));
    }

    [Fact]
    public void ChaCha20加密解密往返()
    {
        var key = CryptoService.GenerateKey(32);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("test data");
        var (ciphertext, nonce, tag) = CryptoService.EncryptChaCha20(plaintext, key);
        var decrypted = CryptoService.DecryptChaCha20(ciphertext, key, nonce, tag);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void 生成密钥长度正确()
    {
        var key = CryptoService.GenerateKey(32);
        Assert.Equal(32, key.Length);
        var key16 = CryptoService.GenerateKey(16);
        Assert.Equal(16, key16.Length);
    }
}
