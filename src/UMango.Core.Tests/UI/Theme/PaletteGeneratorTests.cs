using UMango.Core.UI.Theme;
using Xunit;

namespace UMango.Core.Tests.UI.Theme;

/// <summary>
/// 调色板生成器的测试。
/// </summary>
public class PaletteGeneratorTests
{
    [Fact]
    public void 灰阶包含全部预期的键()
    {
        var palette = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);

        var expected = new[]
        {
            "Gray1", "Gray2", "Gray3", "Gray4", "Gray5", "Gray6", "Gray7", "Gray8",
            "HalfWhite", "SemiWhite", "White", "Transparent",
            "TransparentBackground", "Background", "ToolTip", "RedBack", "Memory",
        };

        foreach (var key in expected)
            Assert.True(palette.ContainsKey(key), $"缺少灰阶键 {key}");
    }

    [Fact]
    public void 彩色阶包含全部预期的键()
    {
        var palette = PaletteGenerator.GenerateColors(ToneProfileConfig.DefaultLight, 235, 0.36, 0.2);

        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "SemiTransparent", "Bg0", "Bg1" })
            Assert.True(palette.ContainsKey(key), $"缺少彩色阶键 {key}");
    }

    [Fact]
    public void 亮色主题的灰阶由暗到亮()
    {
        var palette = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);

        var previous = -1.0;
        for (var i = 1; i <= 8; i++)
        {
            var lightness = OklabColor.FromArgb(palette[$"Gray{i}"]).L;
            Assert.True(lightness > previous, $"Gray{i} 的明度 {lightness} 没有大于上一档 {previous}");
            previous = lightness;
        }
    }

    [Fact]
    public void 暗色主题的灰阶方向与亮色相反()
    {
        var light = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);
        var dark = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultDark);

        // 亮色主题 Gray1 最暗；暗色主题 Gray1 最亮。
        Assert.True(OklabColor.FromArgb(light["Gray1"]).L < OklabColor.FromArgb(light["Gray8"]).L);
        Assert.True(OklabColor.FromArgb(dark["Gray1"]).L > OklabColor.FromArgb(dark["Gray8"]).L);
    }

    [Fact]
    public void 全透明色的alpha为零()
    {
        var palette = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);

        var alpha = (palette["Transparent"] >> 24) & 0xFF;
        Assert.Equal(0, alpha);
    }

    [Fact]
    public void 半透明白的透明度落在一半与全白之间()
    {
        var palette = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);

        var half = (palette["HalfWhite"] >> 24) & 0xFF;
        var semi = (palette["SemiWhite"] >> 24) & 0xFF;

        Assert.InRange(half, 84, 85);
        Assert.InRange(semi, 186, 187);
    }

    [Fact]
    public void 正向调整把颜色往亮处推()
    {
        Assert.True(PaletteGenerator.AdjustLinear(0.5, 0.4) > 0.5);
    }

    [Fact]
    public void 负向调整把颜色往暗处推()
    {
        Assert.True(PaletteGenerator.AdjustLinear(0.5, -0.4) < 0.5);
    }

    [Fact]
    public void 调整量为零时原样返回()
    {
        Assert.Equal(0.5, PaletteGenerator.AdjustLinear(0.5, 0), 10);
    }

    [Fact]
    public void 调整量超出范围时被夹住()
    {
        // 调整量被夹在 -1~1，所以不会把值推出 0~1。
        var result = PaletteGenerator.AdjustLinear(0.5, 5);
        Assert.InRange(result, 0.0, 1.0);
    }

    [Fact]
    public void 不同色相生成的颜色确实不同()
    {
        var blue = PaletteGenerator.GenerateColors(ToneProfileConfig.DefaultLight, 235, 0, 0);
        var red = PaletteGenerator.GenerateColors(ToneProfileConfig.DefaultLight, 30, 0, 0);

        Assert.NotEqual(blue["2"], red["2"]);
    }

    [Fact]
    public void 灰阶的颜色确实是无彩的()
    {
        var palette = PaletteGenerator.GenerateGrays(ToneProfileConfig.DefaultLight);

        foreach (var key in new[] { "Gray1", "Gray4", "Gray8" })
        {
            var chroma = OklabColor.FromArgb(palette[key]).Chroma;
            Assert.True(chroma < 0.02, $"{key} 的彩度 {chroma} 不该那么高");
        }
    }
}
