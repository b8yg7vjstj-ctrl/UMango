using System.Numerics;
using UMango.Core.UI.Blur;
using Xunit;

namespace UMango.Core.Tests.UI.Blur;

/// <summary>
/// 模糊核心算法的测试。
///
/// 全部在 macOS 上跑（Core 的 net10.0 支不依赖 WPF），
/// 这一点本身就是「算法层没有混进 WPF 依赖」的证明。
/// </summary>
public sealed class BlurKernelTests
{
    private static uint[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new uint[width * height];
        var color = BlurKernel.PackColor(a, r, g, b);
        for (var i = 0; i < pixels.Length; i++) pixels[i] = color;
        return pixels;
    }

    [Fact]
    public void 泊松采样点数量固定且都在单位圆内()
    {
        var samples = BlurKernel.GeneratePoissonDiskSamples();

        Assert.Equal(BlurKernel.SampleCount, samples.Length);
        Assert.All(samples, s => Assert.True(s.LengthSquared() <= 1.0001f, $"采样点 {s} 落在单位圆外"));
    }

    [Fact]
    public void 泊松采样点是可复现的()
    {
        // 固定种子 → 每次运行的图案必须完全一样，
        // 否则同一张图两次渲染会得到不同的模糊结果（视觉抖动）。
        var first = BlurKernel.GeneratePoissonDiskSamples();
        var second = BlurKernel.GeneratePoissonDiskSamples();

        Assert.Equal(first, second);
    }

    [Fact]
    public void 泊松采样点两两之间保持最小间距()
    {
        var samples = BlurKernel.GeneratePoissonDiskSamples();
        var minDistance = BlurKernel.MinSampleDistance;

        for (var i = 0; i < samples.Length; i++)
        for (var j = i + 1; j < samples.Length; j++)
        {
            var distance = Vector2.Distance(samples[i], samples[j]);
            Assert.True(distance >= minDistance - 0.0001f,
                $"第 {i} 与 {j} 个采样点间距 {distance} 小于最小间距 {minDistance}");
        }
    }

    [Fact]
    public void 高斯核权重和为一()
    {
        var kernel = BlurKernel.GenerateGaussianKernel(8);

        Assert.Equal(17, kernel.Length);
        Assert.Equal(1.0, kernel.Sum(), 6);
    }

    [Fact]
    public void 高斯核是对称的且中心最大()
    {
        var kernel = BlurKernel.GenerateGaussianKernel(6);

        for (var i = 0; i < kernel.Length / 2; i++)
            Assert.Equal(kernel[i], kernel[kernel.Length - 1 - i], 12);

        Assert.Equal(kernel.Max(), kernel[kernel.Length / 2]);
    }

    [Fact]
    public void 半径为零的核只有一个元素()
    {
        var kernel = BlurKernel.GenerateGaussianKernel(0);

        Assert.Single(kernel);
        Assert.Equal(1.0, kernel[0], 12);
    }

    [Fact]
    public void 负半径会被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurKernel.GenerateGaussianKernel(-1));
    }

    [Fact]
    public void 常量图模糊后不变()
    {
        // 这是最基本的正确性：均匀区域不该被模糊改动。
        // 如果权重归一化写错（比如没除以 totalWeight），
        // 这条会立刻变暗 → 变红。
        var source = Solid(8, 8, 120, 130, 140);
        var target = new uint[source.Length];

        BlurKernel.ApplySeparableBlur(source, target, 8, 8, 3, 1.0);

        Assert.All(target, p => Assert.Equal(source[0], p));
    }

    [Fact]
    public void 阶跃边缘会被抹平()
    {
        // 左半黑右半白。模糊后，过渡带之外的像素应各自保持本色，
        // 而过渡带里的像素应出现中间灰。
        const int width = 16, height = 4;
        var source = new uint[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            source[y * width + x] = BlurKernel.PackColor(255, x < width / 2 ? (byte)0 : (byte)255, x < width / 2 ? (byte)0 : (byte)255, x < width / 2 ? (byte)0 : (byte)255);

        var target = new uint[source.Length];
        BlurKernel.ApplySeparableBlur(source, target, width, height, 3, 1.0);

        // 远离边缘的两端保持原值
        Assert.Equal(0, (byte)(target[0] & 0xFF));
        Assert.Equal(255, (byte)(target[width - 1] & 0xFF));

        // 过渡带里出现了中间值
        var middle = (byte)(target[(width / 2 - 1) * 1] & 0xFF);
        Assert.InRange(middle, (byte)1, (byte)254);
    }

    [Fact]
    public void 半径为零时原样拷贝()
    {
        var source = Solid(4, 4, 10, 20, 30);
        var target = new uint[source.Length];

        BlurKernel.ApplySeparableBlur(source, target, 4, 4, 0, 1.0);

        Assert.Equal(source, target);
    }

    [Fact]
    public void 缓冲过短会被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            BlurKernel.ApplySeparableBlur(new uint[4], new uint[16], 4, 4, 1, 1.0));
        Assert.Throws<ArgumentException>(() =>
            BlurKernel.ApplySeparableBlur(new uint[16], new uint[4], 4, 4, 1, 1.0));
    }

    [Fact]
    public void 采样率低时步长变大会加速但结果更粗糙()
    {
        // 采样率 < 0.8 时按步长跳过部分采样点。
        // 断言的是「步长确实生效」：低采样率下参与计算的样本更少。
        const int width = 32, height = 8;
        var source = new uint[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            source[y * width + x] = BlurKernel.PackColor(255, (byte)(x * 8), 0, 0);

        var full = new uint[source.Length];
        var sparse = new uint[source.Length];
        BlurKernel.ApplySeparableBlur(source, full, width, height, 4, 1.0);
        BlurKernel.ApplySeparableBlur(source, sparse, width, height, 4, 0.3);

        Assert.NotEqual(full, sparse);
    }

    [Fact]
    public void 分离模糊与逐像素采样的常量图结果一致()
    {
        // 两条路径都作用在同一份算法语义上；常量图下两者都应保持原值。
        var source = Solid(6, 6, 77, 88, 99);
        var separable = new uint[source.Length];
        var sampled = new uint[source.Length];

        BlurKernel.ApplySeparableBlur(source, separable, 6, 6, 2, 1.0);
        SamplingBlur.Apply(source, sampled, 6, 6, 2, 1.0);

        Assert.All(separable, p => Assert.Equal(source[0], p));
        Assert.All(sampled, p => Assert.Equal(source[0], p));
    }
}
