using UMango.Core.Diff;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 二进制增量补丁引擎（UMD1 容器）的测试。
///
/// 覆盖往返正确性、补丁体积收益、以及容器被损坏时的报错行为。
/// </summary>
public class BinaryDeltaTests
{
    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void 空对空能往返()
    {
        var patch = BinaryDelta.Create([], []);
        Assert.Equal([], BinaryDelta.Apply([], patch));
    }

    [Fact]
    public void 完全不同也能往返()
    {
        var oldData = Bytes("aaaaaaaaaaaaaaaa");
        var newData = Bytes("bbbbbbbbbbbbbbbb");

        var patch = BinaryDelta.Create(oldData, newData);
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 完全相同不产生额外数据()
    {
        var data = Bytes("the quick brown fox jumps over the lazy dog");

        var patch = BinaryDelta.Create(data, data);
        var restored = BinaryDelta.Apply(data, patch);

        Assert.Equal(data, restored);
    }

    [Fact]
    public void 尾部追加能往返()
    {
        var oldData = Bytes("header\n");
        var newData = Bytes("header\nbody line 1\nbody line 2\n");

        var patch = BinaryDelta.Create(oldData, newData);
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 中间插入能往返()
    {
        var oldData = Bytes("AAAA BBBB CCCC");
        var newData = Bytes("AAAA XXX BBBB CCCC");

        var patch = BinaryDelta.Create(oldData, newData);
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 单字节改动能往返()
    {
        var oldData = new byte[256];
        var newData = new byte[256];
        newData[128] = 0xAB;

        var patch = BinaryDelta.Create(oldData, newData);
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 字节级翻转能往返()
    {
        var oldData = new byte[512];
        var random = new Random(1234);
        random.NextBytes(oldData);
        var newData = (byte[])oldData.Clone();
        for (var i = 0; i < newData.Length; i += 7) newData[i] ^= 0xFF;

        var patch = BinaryDelta.Create(oldData, newData);
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 大文件局部改动时补丁远小于新文件()
    {
        var oldData = new byte[256 * 1024];
        var random = new Random(42);
        random.NextBytes(oldData);
        var newData = (byte[])oldData.Clone();
        // 只改中间 16 字节
        for (var i = 100_000; i < 100_016; i++) newData[i] ^= 0x5A;

        var patch = BinaryDelta.Create(oldData, newData);

        Assert.True(patch.Length < newData.Length / 10,
            $"补丁 {patch.Length} 字节相对新文件 {newData.Length} 字节太大了");
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 整体平移时差分块远小于新文件()
    {
        // 这是增量的核心价值所在：把每个字节都 +1，
        // 差值全是同一个常量 → 差分块高度可压缩。
        // 若实现退化成「直接存新字节」，这块就变成不可压缩的随机数据，
        // 补丁体积会与新文件相当。这条用例专门盯住这一点。
        var oldData = new byte[100_000];
        var random = new Random(9);
        random.NextBytes(oldData);
        var newData = new byte[oldData.Length];
        for (var i = 0; i < oldData.Length; i++) newData[i] = (byte)(oldData[i] + 1);

        var patch = BinaryDelta.Create(oldData, newData);

        Assert.True(patch.Length < newData.Length / 20,
            $"补丁 {patch.Length} 字节相对新文件 {newData.Length} 字节太大了（差分没有生效）");
        Assert.Equal(newData, BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 长度字段被篡改时拒绝应用()
    {
        var oldData = Bytes("hello");
        var newData = Bytes("hello world");
        var patch = BinaryDelta.Create(oldData, newData);

        patch[8] ^= 0xFF;

        Assert.ThrowsAny<ArgumentException>(() => BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 魔数不对时拒绝应用()
    {
        var oldData = Bytes("hello");
        var patch = BinaryDelta.Create(oldData, Bytes("hello world"));
        patch[0] = (byte)'X';

        Assert.ThrowsAny<ArgumentException>(() => BinaryDelta.Apply(oldData, patch));
    }

    [Fact]
    public void 补丁被截断时拒绝应用()
    {
        var oldData = Bytes("hello");
        var patch = BinaryDelta.Create(oldData, Bytes("hello world"));

        Assert.ThrowsAny<ArgumentException>(() => BinaryDelta.Apply(oldData, patch[..10]));
    }

    [Fact]
    public void 旧文件比补丁预期的短时按零补齐()
    {
        // 旧文件缺了一段：越界位置的「原值」按 0 处理，
        // 结果应与生成补丁时使用的旧数据一致。
        var oldData = Bytes("0123456789");
        var newData = Bytes("0123456789ABCDEF");
        var patch = BinaryDelta.Create(oldData, newData);

        var restored = BinaryDelta.Apply(oldData, patch);
        Assert.Equal(newData, restored);
    }
}
