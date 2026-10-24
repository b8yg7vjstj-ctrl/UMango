using UMango.Core.Diff;
using Xunit;

namespace UMango.Core.Tests;

public class BinaryDiffTests
{
    [Fact]
    public void 补丁往返()
    {
        var oldData = System.Text.Encoding.UTF8.GetBytes("hello world");
        var newData = System.Text.Encoding.UTF8.GetBytes("hello universe");
        var patch = BinaryDiff.CreatePatch(oldData, newData);
        var restored = BinaryDiff.ApplyPatch(oldData, patch);
        Assert.Equal(newData, restored);
    }

    [Fact]
    public void 补丁确实小于原文件()
    {
        var oldData = new byte[1024];
        var newData = new byte[1024];
        Array.Copy(new byte[] { 0x01, 0x02, 0x03 }, newData, 3);
        var patch = BinaryDiff.CreatePatch(oldData, newData);
        Assert.True(patch.Length < oldData.Length);
    }

    [Fact]
    public void 门面走的是增量引擎而不是整体压缩()
    {
        // 旧实现是「deflate 压缩整个新文件」—— 那不是增量，
        // 补丁里含全部内容，改一个字节也要重下整个文件。
        // 这里造一个「大文件 + 局部改动」，要求补丁远小于新文件。
        var oldData = new byte[200_000];
        var random = new Random(7);
        random.NextBytes(oldData);
        var newData = (byte[])oldData.Clone();
        newData[100_000] ^= 0xFF;

        var patch = BinaryDiff.CreatePatch(oldData, newData);

        Assert.True(patch.Length < 4096,
            $"补丁 {patch.Length} 字节仍然很大，说明没有真正走增量");
        Assert.Equal(newData, BinaryDiff.ApplyPatch(oldData, patch));
    }

    [Fact]
    public void 旧版deflate补丁仍能应用()
    {
        // 兼容：早期版本产出的补丁是 deflate 压缩的完整新文件。
        var oldData = System.Text.Encoding.UTF8.GetBytes("hello");
        var newData = System.Text.Encoding.UTF8.GetBytes("hello world");

        byte[] legacy;
        using (var output = new System.IO.MemoryStream())
        {
            using (var deflate = new System.IO.Compression.DeflateStream(
                       output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(newData, 0, newData.Length);
            legacy = output.ToArray();
        }

        Assert.Equal(newData, BinaryDiff.ApplyPatch(oldData, legacy));
    }

    [Fact]
    public void IsWorthPatching()
    {
        Assert.True(BinaryDiff.IsWorthPatching(1000, 1100, 100));   // 10%
        Assert.False(BinaryDiff.IsWorthPatching(1000, 2000, 900));  // 90%
    }
}
