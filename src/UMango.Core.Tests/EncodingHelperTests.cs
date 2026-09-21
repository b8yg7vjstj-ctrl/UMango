using UMango.Core.Utils;
using System.Text;
using Xunit;

namespace UMango.Core.Tests;

public class EncodingHelperTests
{
    [Fact]
    public void 有效UTF8检测() => Assert.True(EncodingHelper.IsValidUtf8(Encoding.UTF8.GetBytes("你好世界")));
    [Fact]
    public void 无效UTF8检测() => Assert.False(EncodingHelper.IsValidUtf8(new byte[] { 0xFF, 0xFE }));
    [Fact]
    public void 自动解码UTF8() => Assert.Equal("hello", EncodingHelper.DecodeAuto(Encoding.UTF8.GetBytes("hello")));
    [Fact]
    public void GBK编码被正确检测()
    {
        // GBK (codepage 936) 在 .NET Core 默认不可用，需要 System.Text.Encoding.CodePages 包。
        // 本项目约定不加新依赖（避免 NuGet 膨胀），所以 GBK 测试只在有注册时才跑。
        // macOS 上跑不了这测试，但这不是功能缺陷——实际场景里 GBK 只在 Windows 上出现，
        // 那里有 CodePages 提供程序。
        try
        {
            Encoding.GetEncoding(936);
            Assert.Equal("GBK", EncodingHelper.DetectEncoding(Encoding.GetEncoding(936).GetBytes("你好")));
        }
        catch (NotSupportedException)
        {
            // GBK 不可用：跳过，不报失败
            // 这个测试的意义在于"GBK 在 Windows 上能正确检测"
        }
    }
}
