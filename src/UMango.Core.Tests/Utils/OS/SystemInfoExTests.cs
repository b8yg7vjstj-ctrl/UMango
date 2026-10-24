using UMango.Core.Utils.OS;
using Xunit;

namespace UMango.Core.Tests.Utils.OS;

/// <summary>系统信息采集（诊断用）。</summary>
public class SystemInfoExTests
{
    [Fact]
    public void 位数与架构不矛盾()
    {
        // 64 位进程不可能跑在 32 位操作系统上（除非 WoW64，而那样 OS 位数仍是 64）。
        if (SystemInfoEx.Is32BitSystem)
            Assert.False(Environment.Is64BitOperatingSystem);
    }

    [Fact]
    public void 非Windows平台不使用GBK()
    {
        if (!OperatingSystem.IsWindows())
            Assert.False(SystemInfoEx.IsGBKEncoding);
    }

    [Fact]
    public void 非Windows平台的ANSI代码页为零()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Equal(0, SystemInfoEx.CurrentAnsiCodePage);
    }

    [Fact]
    public void 系统信息描述包含操作系统版本()
    {
        var info = SystemInfoEx.OSInfo;
        Assert.False(string.IsNullOrWhiteSpace(info));
        Assert.Contains(System.Environment.OSVersion.Version.ToString(), info);
    }

    [Fact]
    public void 诊断摘要含位数与核心数()
    {
        var describe = SystemInfoEx.Describe();
        Assert.Contains("位", describe);
        Assert.Contains("核", describe);
        Assert.True(Environment.ProcessorCount >= 1);
    }
}
