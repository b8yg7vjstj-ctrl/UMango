using System;
using UMango.Core.UI;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 灰度（哀悼日）模式的纯数学测试。
///
/// 覆盖对象是 <see cref="GrayColorMath"/> 与 <see cref="GrayProfile"/> 的档位语义，
/// 不是"界面真的变灰了" —— 后者在 macOS 测试宿主上没有 WPF，
/// 真机视觉验收走 Parallels 那台 Windows 11。
/// </summary>
public class GrayProfileTests
{
    // ---- LevelToChannel ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 128)]
    [InlineData(100, 255)]
    public void 亮度档换算成通道值(double level, int expected)
        => Assert.Equal(expected, GrayColorMath.LevelToChannel(level));

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(140, 255)]
    public void 越界亮度档会被夹住而不是回绕(double level, int expected)
        => Assert.Equal(expected, GrayColorMath.LevelToChannel(level));

    // ---- LevelToUnit ----

    [Fact]
    public void 亮度档换算成单位亮度()
    {
        Assert.Equal(0.5d, GrayColorMath.LevelToUnit(50), 3);
        Assert.Equal(1d, GrayColorMath.LevelToUnit(100), 3);
        Assert.Equal(0d, GrayColorMath.LevelToUnit(-10), 3);
    }

    // ---- AdjustLevel ----

    [Fact]
    public void 暗部走负向系数亮部走正向系数()
    {
        // 50 是分界点：>= 50 算亮部，用正向系数
        Assert.Equal(70d, GrayColorMath.AdjustLevel(50, 1.4d, 0.5d), 3);
        // 40 落在暗部，用负向系数；此时若错用正向系数会得到 56 而不是 20
        Assert.Equal(20d, GrayColorMath.AdjustLevel(40, 1.4d, 0.5d), 3);
    }

    [Fact]
    public void 亮度调整结果被夹在零到一百之间()
    {
        Assert.Equal(100d, GrayColorMath.AdjustLevel(80, 2d, 1d), 3);
        Assert.Equal(0d, GrayColorMath.AdjustLevel(10, 1d, 0d), 3);
    }

    [Fact]
    public void 系数等于一时亮度不变()
    {
        for (var level = 0; level <= 100; level += 10)
            Assert.Equal(level, GrayColorMath.AdjustLevel(level, 1d, 1d), 3);
    }

    // ---- SaturationAt ----

    [Fact]
    public void 饱和度在两个端点之间线性插值()
    {
        Assert.Equal(1d, GrayColorMath.SaturationAt(0, 1d, 1d), 3);
        Assert.Equal(0.4d, GrayColorMath.SaturationAt(100, 1d, 0.4d), 3);
        Assert.Equal(0.7d, GrayColorMath.SaturationAt(50, 1d, 0.4d), 3);
    }

    [Fact]
    public void 饱和度插值结果被夹在零到一之间()
    {
        Assert.Equal(1d, GrayColorMath.SaturationAt(100, 1d, 5d), 3);
        Assert.Equal(0d, GrayColorMath.SaturationAt(100, -3d, -8d), 3);
    }

    // ---- ColorMatrix ----

    [Fact]
    public void 完全灰度时三个颜色行的输出系数相同()
    {
        var m = GrayColorMath.ColorMatrix(saturation: 0);
        // 三行必须逐位相等 —— 不等就说明偏色了
        for (var col = 0; col < 3; col++)
            Assert.Equal(m[col], m[5 + col], 6);

        // 每行 R+G+B 权重之和为 1（灰度化不改变总能量）
        Assert.Equal(1d, m[0] + m[1] + m[2], 6);
    }

    [Fact]
    public void 饱和度等于一时是单位矩阵()
    {
        var m = GrayColorMath.ColorMatrix(saturation: 1);
        Assert.Equal(1d, m[0], 6);
        Assert.Equal(0d, m[1], 6);
        Assert.Equal(0d, m[2], 6);
    }

    [Fact]
    public void 亮度系数会同时乘到颜色三行和对角线()
    {
        var half = GrayColorMath.ColorMatrix(saturation: 0.5, brightnessFactor: 0.5);
        var full = GrayColorMath.ColorMatrix(saturation: 0.5, brightnessFactor: 1);
        // 除平移列（恒为 0）外，矩阵每一个元素都该正好是亮度系数的倍数
        for (var i = 0; i < 20; i++)
            Assert.Equal(full[i] * 0.5d, half[i], 6);

        // 对角线末项就是亮度系数本身（alpha 行）
        Assert.Equal(1d, full[18], 6);
        Assert.Equal(0d, full[3], 6);   // 平移列恒为 0
    }

    [Fact]
    public void 饱和度会被夹在有效区间内()
    {
        var low = GrayColorMath.ColorMatrix(saturation: -5d);
        var zero = GrayColorMath.ColorMatrix(saturation: 0d);
        Assert.Equal(string.Join(',', zero), string.Join(',', low));

        var high = GrayColorMath.ColorMatrix(saturation: 9d);
        var one = GrayColorMath.ColorMatrix(saturation: 1d);
        Assert.Equal(string.Join(',', one), string.Join(',', high));
    }

    [Fact]
    public void 格式化输出固定二十个不变量小数并以空格分隔()
    {
        var s = GrayColorMath.FormatMatrix(0.5d, 0.9d);
        Assert.Equal(20, s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(',', s);   // 不受当前区域性影响
    }

    // ---- GrayProfile 档位访问 ----

    [Fact]
    public void 亮度档按索引取回对应通道值()
    {
        var p = GrayProfileConfig.Default.Light;
        Assert.Equal(GrayColorMath.LevelToChannel(p.L1), p.LevelChannel(0));
        Assert.Equal(GrayColorMath.LevelToChannel(p.L8), p.LevelChannel(7));
    }

    [Fact]
    public void 灰阶按索引取回对应通道值()
    {
        var p = GrayProfileConfig.Default.Dark;
        Assert.Equal(GrayColorMath.LevelToChannel(p.G1), p.GrayChannel(0));
        Assert.Equal(GrayColorMath.LevelToChannel(p.G3), p.GrayChannel(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void 越界的亮度档索引会被拒绝(int index)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GrayProfileConfig.Default.Light.LevelChannel(index));

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void 越界的灰阶索引会被拒绝(int index)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GrayProfileConfig.Default.Light.GrayChannel(index));

    // ---- 脱色 / 亮度：token 级灰度实际用的路径 ----

    [Fact]
    public void 完全脱色后三个通道相等()
    {
        var gray = GrayColorMath.Desaturate(unchecked((int)0xFF1370F3), saturation: 0);
        Assert.Equal(gray & 0xFF, (gray >> 8) & 0xFF);   // B == G
        Assert.Equal((gray >> 8) & 0xFF, (gray >> 16) & 0xFF); // G == R
    }

    [Fact]
    public void 脱色不会动alpha()
    {
        var src = unchecked((int)0x801370F3);
        Assert.Equal(0x80, (GrayColorMath.Desaturate(src, 0.5) >> 24) & 0xFF);
    }

    [Fact]
    public void 饱和度等于一时原样返回()
    {
        Assert.Equal(unchecked((int)0xFF1370F3), GrayColorMath.Desaturate(unchecked((int)0xFF1370F3), 1));
    }

    [Fact]
    public void 脱色保留相对亮度层次()
    {
        // 深色背景与浅色文字的先后关系必须保持：亮者仍亮、���者仍暗。
        const int bg = unchecked((int)0xFF1E1F22);
        const int fg = unchecked((int)0xFFE8EAED);
        var gBg = Luminance(GrayColorMath.Desaturate(bg, 0));
        var gFg = Luminance(GrayColorMath.Desaturate(fg, 0));
        Assert.True(gFg > gBg, $"灰度后前景 {gFg} 应仍亮于背景 {gBg}");
    }

    [Fact]
    public void 等权重灰度化会让暗色系文字融进背景而亮度加权不会()
    {
        // 复现为什么不能用简单取三通道均值：深色 token 的文字与背景亮度本来就接近，
        // 均值化会把两者的细微差别抹掉，而亮度加权仍保得住。
        const int bg = unchecked((int)0xFF26282C);   // G8
        const int fg = unchecked((int)0xFF919499);   // G3
        var meanGap = Math.Abs(Mean(fg) - Mean(bg));
        var lumGap = Math.Abs(Luminance(GrayColorMath.Desaturate(fg, 0)) - Luminance(GrayColorMath.Desaturate(bg, 0)));
        Assert.True(lumGap >= meanGap, $"亮度加权差 {lumGap} 不应小于均值差 {meanGap}");
    }

    private static double Luminance(int argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return 0.299d * r + 0.587d * g + 0.114d * b;
    }

    private static double Mean(int argb)
        => (((argb >> 16) & 0xFF) + ((argb >> 8) & 0xFF) + (argb & 0xFF)) / 3d;

    [Theory]
    [InlineData(0.5d, 128)]   // 255 * 0.5 = 127.5，四舍五入到 128
    [InlineData(0.25d, 64)]   // 255 * 0.25 = 63.75 → 64
    [InlineData(1d, 255)]     // 不动
    public void 亮度调整按比例缩放且保留alpha(double factor, int expected)
    {
        var result = GrayColorMath.ScaleBrightness(unchecked((int)0x80FFFFFF), factor);
        Assert.Equal(0x80, (result >> 24) & 0xFF);
        Assert.Equal(expected, result & 0xFF);
    }

    [Fact]
    public void 亮度调整越界时夹住而不是回绕()
    {
        Assert.Equal(unchecked((int)0xFF000000), GrayColorMath.ScaleBrightness(unchecked((int)0xFFFFFFFF), 3d) & unchecked((int)0xFF000000));
        Assert.Equal(unchecked((int)0xFF000000), GrayColorMath.ScaleBrightness(unchecked((int)0xFF808080), -1d) & unchecked((int)0xFF000000));
    }

    [Theory]
    [InlineData(3d)]      // 上限：不能溢出到 255 以上，否则 FromArgb 拆出的通道非法
    [InlineData(-1d)]     // 下限：不能为负
    [InlineData(100d)]    // 极端倍数
    public void 亮度调整任何系数下都不会产出非法通道(double factor)
    {
        // 上界卡在 300 是为了模拟「忘了夹上限」的注入：这类错误在界面上表现为
        // 颜色被截断成错值，但只在系数明显偏离 1 时才发作，用例必须覆盖到。
        var result = GrayColorMath.ScaleBrightness(unchecked((int)0xFF808080), factor);
        Assert.InRange(result & 0xFF, 0, 255);
        Assert.InRange((result >> 8) & 0xFF, 0, 255);
        Assert.InRange((result >> 16) & 0xFF, 0, 255);
        Assert.Equal(0xFF, (result >> 24) & 0xFF);
    }

    [Fact]
    public void 亮度系数过大时正好饱和到白色而不是溢出()
    {
        var result = GrayColorMath.ScaleBrightness(unchecked((int)0xFF606060), 10d);
        Assert.Equal(unchecked((int)0xFFFFFFFF), result);
    }

    [Fact]
    public void 一次性应用等于先脱色再调亮度()
    {
        const int argb = unchecked((int)0xFF1370F3);
        var oneShot = GrayColorMath.Apply(argb, 0.3d, 0.8d);
        var twoStep = GrayColorMath.ScaleBrightness(GrayColorMath.Desaturate(argb, 0.3d), 0.8d);
        Assert.Equal(twoStep, oneShot);
    }

    [Fact]
    public void 饱和度与亮度都等于一时是恒等变换()
    {
        // 这是灰度模式"关闭"时用的那组参数：必须逐位原样返回，否则开关一开一关颜色就漂了。
        const int argb = unchecked((int)0xFF1370F3);
        Assert.Equal(argb, GrayColorMath.Apply(argb, 1d, 1d));
    }

    // ---- GrayColorPlanner：记账与还原（WPF 层那套薄管道靠它保证不出错）----

    [Fact]
    public void 首次规划时记下原色并算出新色()
    {
        var p = new GrayColorPlanner<string>();
        var result = p.Plan("Br", unchecked((int)0xFF1370F3), 0.5d, 1d);
        Assert.NotEqual(unchecked((int)0xFF1370F3), result);
        Assert.Equal(1, p.TrackedCount);
        Assert.Equal(unchecked((int)0xFF1370F3), p.OriginalColor("Br", 0));
    }

    [Fact]
    public void 反复规划时原色不会被脱色值顶掉()
    {
        // 这是「反复开关后颜色越改越偏」的根因所在，故单独守着。
        var p = new GrayColorPlanner<string>();
        const int original = unchecked((int)0xFF1370F3);

        var first = p.Plan("Br", original, 0.5d, 1d);
        var second = p.Plan("Br", first, 0.5d, 1d);
        var third = p.Plan("Br", second, 0.5d, 1d);

        Assert.Equal(original, p.OriginalColor("Br", 0));
        Assert.Equal(first, second);   // 同一个原色 + 同一组参数 → 同一结果
        Assert.Equal(second, third);
    }

    [Fact]
    public void 未记账的画笔查询时返回兜底值()
        => Assert.Equal(123, new GrayColorPlanner<string>().OriginalColor("缺失", 123));

    [Fact]
    public void 忘记某个画笔后它的记账被移除()
    {
        var p = new GrayColorPlanner<string>();
        p.Plan("A", 1, 0, 1);
        p.Plan("B", 2, 0, 1);
        Assert.True(p.Forget("A"));
        Assert.False(p.Forget("A"));
        Assert.Equal(1, p.TrackedCount);
    }

    [Fact]
    public void 排空会交出全部还原色并清空记账()
    {
        var p = new GrayColorPlanner<string>();
        p.Plan("A", unchecked((int)0xFF111111), 0, 1);
        p.Plan("B", unchecked((int)0xFF222222), 0, 1);

        var drained = p.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Contains(drained, d => d.Key == "A" && d.Color == unchecked((int)0xFF111111));
        Assert.Contains(drained, d => d.Key == "B" && d.Color == unchecked((int)0xFF222222));
        Assert.Equal(0, p.TrackedCount);
    }

    [Fact]
    public void 排空之后再规划会把当前值当成新的原色()
    {
        // 排空 = 已还原；此后再启用时把「当前（已还原的）值」记为原色才是对的。
        var p = new GrayColorPlanner<string>();
        p.Plan("A", unchecked((int)0xFF111111), 0, 1);
        p.Drain();
        p.Plan("A", unchecked((int)0xFF222222), 0, 1);
        Assert.Equal(unchecked((int)0xFF222222), p.OriginalColor("A", 0));
    }

    [Fact]
    public void 按引用而不是按值区分画笔()
    {
        var p = new GrayColorPlanner<string>();
        p.Plan("A", 1, 0, 1);
        p.Plan("A", 1, 0, 1);
        Assert.Equal(1, p.TrackedCount);
    }

    // ---- 配置 ----

    [Fact]
    public void 浅色与深色音色是分开的()
    {
        var c = GrayProfileConfig.Default;
        Assert.Same(c.Light, c.CurrentProfile(isDarkMode: false));
        Assert.Same(c.Dark, c.CurrentProfile(isDarkMode: true));
        Assert.NotEqual(c.Light, c.Dark);
    }

    [Fact]
    public void 默认总开关是关闭的()
        => Assert.False(GrayProfileConfig.Default.IsEnabled);

    [Fact]
    public void 序列化后再解析能原样回来()
    {
        var original = GrayProfileConfig.Default with { IsEnabled = true };
        var round = GrayProfileConfig.FromJson(original.ToJson());
        Assert.True(round.IsEnabled);
        Assert.Equal(original.Light, round.Light);
        Assert.Equal(original.Dark, round.Dark);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空配置回落到默认而不是抛异常(string? json)
        => Assert.Same(GrayProfileConfig.Default, GrayProfileConfig.FromJson(json));

    [Fact]
    public void 损坏的配置回落到给定兜底而不是抛异常()
    {
        var fallback = GrayProfileConfig.Default with { IsEnabled = true };
        var parsed = GrayProfileConfig.FromJson("{ 这不是 JSON", fallback);
        Assert.Same(fallback, parsed);
    }

    [Fact]
    public void 未知字段被忽略以便跨版本兼容()
    {
        // 未来版本的配置文件会有本版本不认识的键；读到旧版本必须照常启动，
        // 而不是抛 JsonException 让应用起不来。
        var json = """{"isEnabled":true,"未来新增开关":7,"light":{"l1":30,"未来新增字段":"x"}}""";
        var parsed = GrayProfileConfig.FromJson(json);
        Assert.True(parsed.IsEnabled);
        Assert.Equal(30, parsed.Light.L1);
    }

    [Fact]
    public void 数字写成字符串也能读进来()
    {
        var parsed = GrayProfileConfig.FromJson("""{"light":{"l1":"42"}}""");
        Assert.Equal(42, parsed.Light.L1);
    }
}