using System.Text;
using UMango.Core.Hash;
using Xunit;

namespace UMango.Core.Tests.Hash;

/// <summary>
/// 哈希算法族（PCL.Core/Utils/Hash，Apache-2.0）的测试。
/// </summary>
public class HashProviderTests
{
    [Theory]
    [InlineData(typeof(MD5Provider), 16)]
    [InlineData(typeof(SHA1Provider), 20)]
    [InlineData(typeof(SHA256Provider), 32)]
    [InlineData(typeof(SHA512Provider), 64)]
    [InlineData(typeof(MurmurHash2Provider), 8)]
    public void 长度_按字节数报而不是十六进制字符数(Type providerType, int expectedLength)
    {
        var provider = (IHashProvider)Activator.CreateInstance(providerType)!;
        Assert.Equal(expectedLength, provider.Length);
        Assert.Equal(expectedLength, provider.ComputeHash("abc").Length);
    }

    [Fact]
    public void 各算法_对同一输入产出稳定结果()
    {
        var data = Encoding.UTF8.GetBytes("UMango 包管理器");

        Assert.Equal(32, Convert.ToHexString(MD5Provider.Instance.ComputeHash(data)).Length);
        Assert.Equal(40, Convert.ToHexString(SHA1Provider.Instance.ComputeHash(data)).Length);
        Assert.Equal(64, Convert.ToHexString(SHA256Provider.Instance.ComputeHash(data)).Length);
        Assert.Equal(128, Convert.ToHexString(SHA512Provider.Instance.ComputeHash(data)).Length);
        Assert.Equal(16, Convert.ToHexString(MurmurHash2Provider.Instance.ComputeHash(data)).Length);
    }

    [Fact]
    public void MD5_与已知标准向量一致()
    {
        // "abc" 的 MD5 是众所周知的标准值
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", Convert.ToHexString(MD5Provider.Instance.ComputeHash("abc")).ToLowerInvariant());
        // 空串的 MD5
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Convert.ToHexString(MD5Provider.Instance.ComputeHash("")).ToLowerInvariant());
    }

    [Fact]
    public void SHA256_与已知标准向量一致()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Convert.ToHexString(SHA256Provider.Instance.ComputeHash("abc")).ToLowerInvariant());
    }

    [Fact]
    public void MurmurHash2_剔除空白字节所以换行不影响结果()
    {
        var a = MurmurHash2Provider.Instance.ComputeHash("hello world");
        var b = MurmurHash2Provider.Instance.ComputeHash("hello\r\nworld");
        Assert.Equal(a, b);
    }

    [Fact]
    public void MurmurHash2_原始模式不做任何过滤()
    {
        var provider = MurmurHash2Provider.Instance;
        var withSpace = provider.ComputeHashRaw("a b"u8);
        var withoutSpace = provider.ComputeHashRaw("ab"u8);
        Assert.NotEqual(withSpace, withoutSpace);
    }

    [Fact]
    public void MurmurHash2_空输入不崩()
    {
        var result = MurmurHash2Provider.Instance.ComputeHashRaw(ReadOnlySpan<byte>.Empty);
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void 流式计算_与一次性计算结果一致()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 200_000) + "尾部标记");
        using var ms = new MemoryStream(data);

        var streamed = MurmurHash2Provider.Instance.ComputeHash(ms);
        var oneShot = MurmurHash2Provider.Instance.ComputeHash(data);

        Assert.Equal(oneShot, streamed);
    }

    [Fact]
    public async Task 流式异步计算_与一次性计算结果一致()
    {
        var data = Encoding.UTF8.GetBytes(new string('y', 150_000));
        using var ms = new MemoryStream(data);

        var streamed = await MurmurHash2Provider.Instance.ComputeHashAsync(ms);
        var oneShot = MurmurHash2Provider.Instance.ComputeHash(data);

        Assert.Equal(oneShot, streamed);
    }

    [Fact]
    public void 文本接口_默认UTF8且可用其它编码()
    {
        var utf8 = SHA256Provider.Instance.ComputeHash("你好");
        var explicitUtf8 = SHA256Provider.Instance.ComputeHash("你好", Encoding.UTF8);
        Assert.Equal(utf8, explicitUtf8);

        var latin = SHA256Provider.Instance.ComputeHash("你好", Encoding.Latin1);
        Assert.NotEqual(utf8, latin);
    }
}
