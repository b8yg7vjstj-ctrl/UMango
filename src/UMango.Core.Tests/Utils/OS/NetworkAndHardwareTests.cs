using System.Net;
using UMango.Core.Utils.OS;
using Xunit;

namespace UMango.Core.Tests.Utils.OS;

/// <summary>网络与硬件信息采集。</summary>
public class NetworkAndHardwareTests
{
    [Fact]
    public void 本机地址不含回环与未指定地址()
    {
        foreach (var address in NetworkUtils.GetAllLocalAddress())
        {
            Assert.NotEqual(IPAddress.Any, address);
            Assert.NotEqual(IPAddress.IPv6Any, address);
            Assert.NotEqual(IPAddress.Loopback, address);
            Assert.NotEqual(IPAddress.IPv6Loopback, address);
        }
    }

    [Fact]
    public void 文本形态与对象形态数量一致()
    {
        var objects = NetworkUtils.GetAllLocalAddress();
        var texts = NetworkUtils.GetAllLocalAddressText();
        Assert.Equal(objects.Length, texts.Length);
        for (var i = 0; i < objects.Length; i++)
            Assert.Equal(objects[i].ToString(), texts[i]);
    }

    [Fact]
    public void 取地址不会抛异常()
    {
        // 连续取两次都不该抛 —— 网络枚举在受限环境里可能失败，
        // 实现必须把异常吞掉并返回空数组。
        var first = NetworkUtils.GetAllLocalAddress();
        var second = NetworkUtils.GetAllLocalAddress();
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void 硬件快照含处理器数与内存()
    {
        var snapshot = HardwareInfo.Collect();
        Assert.True(snapshot.ProcessorCount >= 1);
        Assert.True(snapshot.PhysicalMemoryBytes > 0);
        Assert.True(snapshot.PhysicalMemoryMiB > 0);
    }

    [Fact]
    public void 硬件快照可重复读取且稳定()
    {
        var a = HardwareInfo.Current;
        var b = HardwareInfo.Current;
        Assert.Equal(a.ProcessorCount, b.ProcessorCount);
        Assert.Equal(a.CpuName, b.CpuName);
    }

    [Fact]
    public void CPU型号读不到时给中性描述而不是空串()
    {
        var name = HardwareInfo.ReadCpuName();
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void 退化用的CPU描述非空()
    {
        // 退化分支在真实机器上几乎走不到（探测总能成功），
        // 所以单独断言它，否则这条路径上的缺陷永远测不出来。
        var fallback = HardwareInfo.FallbackCpuName;
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.Contains("处理器", fallback);
    }
}
