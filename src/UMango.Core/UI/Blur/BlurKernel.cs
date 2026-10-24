// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Effects/SamplingBlurProcessor.cs 中的算法部分采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Effects → UMango.Core.UI.Blur；
//   2. **剥离 WPF 依赖**：原类的输入输出是 BitmapSource / WriteableBitmap，
//      内部用 unsafe 指针拷贝像素。这里改成纯托管的 uint[] 像素缓冲
//      （BGRA32，每像素一个 uint，布局与 WPF 的 Bgra32 一致），
//      于是整套算法可以在 Core 的 net10.0 支上编译、并在 macOS 上被测试；
//   3. 原类的缓存（ConcurrentDictionary + 过期清理）移到 WPF 适配层，
//      Core 这层只做纯粹的「像素进、像素出」计算；
//   4. 原版的 PixelFormats / WriteableBitmap / unsafe 块全部不需要。

using System.Numerics;

namespace UMango.Core.UI.Blur;

/// <summary>
/// 模糊核心算法。纯计算，不涉及任何 WPF 类型。
///
/// 像素缓冲约定：每像素一个 <c>uint</c>，按 BGRA32 排列
/// （<c>0xAABBGGRR</c>，与 WPF 的 <c>PixelFormats.Bgra32</c> 一致）。
/// </summary>
public static class BlurKernel
{
    /// <summary>泊松盘采样点数量。</summary>
    public const int SampleCount = 32;

    /// <summary>
    /// 采样点之间保证的最小间距。
    /// </summary>
    /// <remarks>
    /// 原版取 0.7 —— 但单位圆内半径 0.7 的圆**最多只能放 6 个互不重叠的采样点**，
    /// 32 个采样点在几何上根本放不下。原版的拒绝采样跑了 1000 次只拿到 6 个
    /// 合法点，剩下 26 个由「极坐标环形补齐」硬塞进去，必然违反最小间距。
    /// 这里改用 Vogel 螺旋（黄金角分布）：32 个点在单位圆内的实际最小间距是
    /// 0.2733，因此下限取 0.27。
    /// </remarks>
    public const float MinSampleDistance = 0.27f;

    /// <summary>固定随机种子 —— 保证每次运行的采样图案完全一致（可复现）。</summary>
    public const int SampleSeed = 42;

    /// <summary>生成采样点（单位圆内均匀分布）。</summary>
    /// <remarks>
    /// 改用 **Vogel 螺旋**（黄金角分布）而非原版的拒绝采样。
    /// 理由见 <see cref="MinSampleDistance"/>：原版的最小间距在几何上不可行。
    /// 黄金角螺旋是解析式分布，不依赖随机数，因此**天然可复现**，
    /// 且点分布比拒绝采样更均匀（无聚集空洞）。
    /// </remarks>
    public static Vector2[] GeneratePoissonDiskSamples()
    {
        var samples = new Vector2[SampleCount];
        var goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));

        for (var i = 0; i < SampleCount; i++)
        {
            // 半径按 sqrt 均匀分布，使面积密度均匀；偏移半个格子避免正好落在圆心。
            var radius = Math.Sqrt((i + 0.5) / SampleCount);
            var angle = i * goldenAngle;
            samples[i] = new Vector2(
                (float)(radius * Math.Cos(angle)),
                (float)(radius * Math.Sin(angle)));
        }

        return samples;
    }

    /// <summary>
    /// 生成归一化的高斯卷积核（长度 2*radius+1，权重和为 1）。
    /// </summary>
    /// <remarks>
    /// 原版用 <c>sigma = radius / 3.0</c>（即 ±3σ 覆盖整个核），
    /// 并显式做了一次归一化。两者都保留 —— 归一化是必要的，
    /// 否则边缘处（采样点被裁掉）会整体变暗。
    /// </remarks>
    public static double[] GenerateGaussianKernel(int radius)
    {
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));

        var size = radius * 2 + 1;
        var kernel = new double[size];
        if (radius == 0)
        {
            // 半径为 0 时 sigma = 0，高斯公式会算出 0/0 = NaN。
            // 数学上正确的核是「什么都不做」的单点核。
            kernel[0] = 1.0;
            return kernel;
        }

        var sigma = radius / 3.0;
        var twoSigmaSquared = 2.0 * sigma * sigma;
        var normalization = 1.0 / Math.Sqrt(Math.PI * twoSigmaSquared);
        double totalWeight = 0;

        for (var i = 0; i < size; i++)
        {
            var x = i - radius;
            var weight = normalization * Math.Exp(-(x * x) / twoSigmaSquared);
            kernel[i] = weight;
            totalWeight += weight;
        }

        if (totalWeight > 0)
        {
            var invTotal = 1.0 / totalWeight;
            for (var i = 0; i < size; i++) kernel[i] *= invTotal;
        }

        return kernel;
    }

    /// <summary>
    /// 双通道分离高斯模糊：先水平、再垂直。
    /// </summary>
    /// <param name="source">源像素（BGRA32）。</param>
    /// <param name="target">输出像素，长度须与 source 相同。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="radius">模糊半径（像素）。</param>
    /// <param name="samplingRate">采样率 0~1；&lt;0.8 时按步长跳过部分采样点。</param>
    public static void ApplySeparableBlur(uint[] source, uint[] target, int width, int height, int radius, double samplingRate)
    {
        if (source.Length < width * height) throw new ArgumentException("源缓冲过短。", nameof(source));
        if (target.Length < width * height) throw new ArgumentException("目标缓冲过短。", nameof(target));
        if (radius <= 0)
        {
            Array.Copy(source, target, width * height);
            return;
        }

        var weights = GenerateGaussianKernel(radius);
        var kernelRadius = weights.Length / 2;
        var sampleStep = samplingRate >= 0.8 ? 1 : (int)Math.Ceiling(2.0 - samplingRate);

        var temp = new uint[width * height];
        ApplyHorizontal(source, temp, width, height, weights, kernelRadius, sampleStep);
        ApplyVertical(temp, target, width, height, weights, kernelRadius, sampleStep);
    }

    /// <summary>水平方向卷积。</summary>
    public static void ApplyHorizontal(uint[] source, uint[] target, int width, int height, double[] weights, int kernelRadius, int sampleStep)
    {
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                double totalA = 0, totalR = 0, totalG = 0, totalB = 0, totalWeight = 0;

                for (var k = -kernelRadius; k <= kernelRadius; k += sampleStep)
                {
                    var sampleX = Math.Max(0, Math.Min(width - 1, x + k));
                    var pixel = source[rowStart + sampleX];
                    var weight = weights[Math.Abs(k) + kernelRadius];

                    totalA += ((pixel >> 24) & 0xFF) * weight;
                    totalR += ((pixel >> 16) & 0xFF) * weight;
                    totalG += ((pixel >> 8) & 0xFF) * weight;
                    totalB += (pixel & 0xFF) * weight;
                    totalWeight += weight;
                }

                target[rowStart + x] = totalWeight > 0
                    ? PackColor(
                        ToByte(totalA / totalWeight),
                        ToByte(totalR / totalWeight),
                        ToByte(totalG / totalWeight),
                        ToByte(totalB / totalWeight))
                    : source[rowStart + x];
            }
        }
    }

    /// <summary>垂直方向卷积。</summary>
    public static void ApplyVertical(uint[] source, uint[] target, int width, int height, double[] weights, int kernelRadius, int sampleStep)
    {
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                double totalA = 0, totalR = 0, totalG = 0, totalB = 0, totalWeight = 0;

                for (var k = -kernelRadius; k <= kernelRadius; k += sampleStep)
                {
                    var sampleY = Math.Max(0, Math.Min(height - 1, y + k));
                    var pixel = source[sampleY * width + x];
                    var weight = weights[Math.Abs(k) + kernelRadius];

                    totalA += ((pixel >> 24) & 0xFF) * weight;
                    totalR += ((pixel >> 16) & 0xFF) * weight;
                    totalG += ((pixel >> 8) & 0xFF) * weight;
                    totalB += (pixel & 0xFF) * weight;
                    totalWeight += weight;
                }

                target[y * width + x] = totalWeight > 0
                    ? PackColor(
                        ToByte(totalA / totalWeight),
                        ToByte(totalR / totalWeight),
                        ToByte(totalG / totalWeight),
                        ToByte(totalB / totalWeight))
                    : source[y * width + x];
            }
        }
    }

    /// <summary>
    /// 把卷积结果收敛成 0~255 的字节。
    /// </summary>
    /// <remarks>
    /// 这里必须**四舍五入**而不是截断。截断（直接转 byte）会在浮点略小于整数时
    /// 把 255 变成 254 —— 常量图经过一次分离卷积就整体暗一档，
    /// 两次（水平 + 垂直）就暗两档。实测：常量图 (255,77,88,99)
    /// 经分离卷积后 alpha 变成 253。
    /// </remarks>
    public static byte ToByte(double value)
    {
        if (double.IsNaN(value)) return 0;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= 0) return 0;
        if (rounded >= 255) return 255;
        return (byte)rounded;
    }

    /// <summary>把四个通道打包成一个 BGRA32 像素。</summary>
    public static uint PackColor(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
}
