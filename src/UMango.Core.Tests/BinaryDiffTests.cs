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
    public void IsWorthPatching()
    {
        Assert.True(BinaryDiff.IsWorthPatching(1000, 1100, 100));   // 10%
        Assert.False(BinaryDiff.IsWorthPatching(1000, 2000, 900));  // 90%
    }
}
