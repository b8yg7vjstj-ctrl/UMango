using UMango.Core.Security;
using Xunit;

namespace UMango.Core.Tests.Security;

/// <summary>
/// 加密提供者与密文容器的测试。
/// </summary>
public sealed class EncryptionProviderTests
{
    public static TheoryData<IEncryptionProvider> Providers => new()
    {
        AesGcmProvider.Instance,
        ChaCha20Poly1305Provider.Instance,
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void 加密后能解回原文(IEncryptionProvider provider)
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var data = "机密内容 secret"u8.ToArray();

        var encrypted = provider.Encrypt(data, key);

        Assert.NotEqual(data, encrypted);
        Assert.Equal(data, provider.Decrypt(encrypted, key));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 两次加密同一明文得到不同密文(IEncryptionProvider provider)
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var data = "same"u8.ToArray();

        Assert.NotEqual(provider.Encrypt(data, key), provider.Encrypt(data, key));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 密文被篡改会被检测出来(IEncryptionProvider provider)
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var encrypted = provider.Encrypt("data"u8, key);
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<Exception>(() => provider.Decrypt(encrypted, key));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 用错误的密钥解不开(IEncryptionProvider provider)
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var other = new byte[32];
        Random.Shared.NextBytes(other);
        var encrypted = provider.Encrypt("data"u8, key);

        Assert.ThrowsAny<Exception>(() => provider.Decrypt(encrypted, other));
    }

    [Fact]
    public void 纯软件ChaCha20能加解密()
    {
        // 兜底实现没有认证码，但加解密本身必须正确
        var provider = ChaCha20SoftwareProvider.Instance;
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var data = "software"u8.ToArray();

        var encrypted = provider.Encrypt(data, key);

        Assert.Equal(data, provider.Decrypt(encrypted, key));
    }

    [Fact]
    public void 密文容器能往返()
    {
        var original = new EncryptionData(2, new byte[] { 1, 2, 3 });

        var bytes = original.ToBytes();
        var restored = EncryptionData.FromBytes(bytes);

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Data, restored.Data);
    }

    [Fact]
    public void 密文容器能识别自己的格式()
    {
        var bytes = new EncryptionData(1, new byte[] { 9 }).ToBytes();

        Assert.True(EncryptionData.IsValid(bytes));
        Assert.False(EncryptionData.IsValid(new byte[] { 1, 2, 3 }));
        Assert.False(EncryptionData.IsValid(Array.Empty<byte>()));
    }

    [Fact]
    public void 长度字段撒谎时会被拒绝()
    {
        // 头里写的长度比实际数据长 —— 不能读出越界的内容
        var bytes = new EncryptionData(1, new byte[] { 1, 2, 3 }).ToBytes();
        bytes[11] = 200;

        Assert.Throws<ArgumentException>(() => EncryptionData.FromBytes(bytes));
    }

    [Fact]
    public void 长度字段为负时会被拒绝()
    {
        var bytes = new EncryptionData(1, new byte[] { 1, 2, 3 }).ToBytes();
        bytes[11] = 0xFF;

        Assert.Throws<ArgumentException>(() => EncryptionData.FromBytes(bytes));
    }

    [Fact]
    public void 魔数不对时会被拒绝()
    {
        var bytes = new EncryptionData(1, new byte[] { 1, 2, 3 }).ToBytes();
        bytes[0] ^= 0xFF;

        Assert.Throws<ArgumentException>(() => EncryptionData.FromBytes(bytes));
    }
}
