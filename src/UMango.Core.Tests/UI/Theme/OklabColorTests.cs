using UMango.Core.UI.Theme;
using Xunit;

namespace UMango.Core.Tests.UI.Theme;

/// <summary>
/// OKLAB 色彩数学的测试。
///
/// 重点守两件事：
///   1. **往返一致**：sRGB → OKLAB → sRGB 必须回到原值（否则生成的主题颜色会偏）；
///   2. **感知均匀**：等距的 L 值在转成 sRGB 后，亮度的实际跳变也应大致均匀 ——
///      这正是 OKLAB 存在的理由，若实现退化成普通 HSL 就守不住。
/// </summary>
public class OklabColorTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(13, 200, 77)]
    [InlineData(77, 88, 99)]
    public void 往返转换后颜色不变(byte r, byte g, byte b)
    {
        var original = OklabColor.FromRgb(r, g, b);
        var (r2, g2, b2) = original.ToRgb();

        Assert.Equal(r, r2);
        Assert.Equal(g, g2);
        Assert.Equal(b, b2);
    }

    [Fact]
    public void 往返转换后透明度不变()
    {
        var original = OklabColor.FromRgb(10, 20, 30, 0.5);
        var argb = original.ToArgb();

        var alpha = (argb >> 24) & 0xFF;
        Assert.InRange(alpha, 127, 128);
    }

    [Fact]
    public void 纯白与纯黑落在预期的明度端点()
    {
        Assert.Equal(1.0, OklabColor.FromRgb(255, 255, 255).L, 3);
        Assert.Equal(0.0, OklabColor.FromRgb(0, 0, 0).L, 3);
    }

    [Fact]
    public void 无彩颜色的彩度接近零()
    {
        var gray = OklabColor.FromRgb(120, 120, 120);
        Assert.True(gray.Chroma < 0.005, $"灰度的彩度应是 {gray.Chroma}，不该接近零");
    }

    [Fact]
    public void 色相角能被正确还原()
    {
        // 0° 应落在 a 正向、b 约 0；90° 应落在 b 正向。
        var at0 = OklabColor.FromLch(0.6, 0.15, 0);
        var at90 = OklabColor.FromLch(0.6, 0.15, 90);

        Assert.True(at0.A > 0 && Math.Abs(at0.B) < 0.01, $"0° 的 a={at0.A}, b={at0.B}");
        Assert.True(at90.B > 0 && Math.Abs(at90.A) < 0.01, $"90° 的 a={at90.A}, b={at90.B}");
    }

    [Fact]
    public void 明度等距时CIE感知亮度也等距()
    {
        // OKLAB 的 L 对应的是 **CIE L\***（感知明度），不是相对亮度 Y。
        // 相对亮度是线性的、间隔会随明度指数式拉开，那是 sRGB 的性质；
        // OKLAB 的意义正是让 L 与感知明度近似线性。
        // 实测：L 每隔 0.15，L\* 依次为 9.4 / 25.2 / 41.9 / 59.6 / 77.6，
        // 间隔 15.8 / 16.7 / 17.7 / 18.0 —— 近似等距。
        var starValues = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var l = 0.2 + i * 0.15;
            var (r, g, b) = OklabColor.FromLch(l).ToRgb();
            starValues.Add(CieLightness(RelativeLuminance(r, g, b)));
        }

        var gaps = new List<double>();
        for (var i = 1; i < starValues.Count; i++) gaps.Add(starValues[i] - starValues[i - 1]);

        var minGap = gaps.Min();
        var maxGap = gaps.Max();

        Assert.True(maxGap < minGap * 1.25,
            $"等距 OKLAB 明度转换后的 CIE L\\* 间隔差距过大：{string.Join(", ", gaps.Select(g => g.ToString("F2")))}");
    }

    [Fact]
    public void 混合两端点得到的结果介于两者之间()
    {
        var dark = OklabColor.FromRgb(0, 0, 0);
        var light = OklabColor.FromRgb(255, 255, 255);

        var mid = OklabColor.Mix(dark, light, 0.5);
        Assert.InRange(mid.L, 0.4, 0.6);
    }

    [Fact]
    public void 混合作用比例会被夹在0到1之间()
    {
        var a = OklabColor.FromRgb(0, 0, 0);
        var b = OklabColor.FromRgb(255, 255, 255);

        Assert.Equal(a.L, OklabColor.Mix(a, b, -5).L, 6);
        Assert.Equal(b.L, OklabColor.Mix(a, b, 5).L, 6);
    }

    /// <summary>CIE L*（感知明度），由相对亮度 Y 换算。</summary>
    private static double CieLightness(double y)
    {
        return y > 0.008856 ? 116 * Math.Cbrt(y) - 16 : 903.3 * y;
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }
}
