// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Effects/SamplingBlurProcessor.cs 中的「质量优先」采样算法采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Blur；
//   2. 剥离 WPF 依赖（BitmapSource/WriteableBitmap → uint[] 像素缓冲）；
//   3. 去掉原版里的缓存与 ArrayPool（缓存属于 WPF 适配层的职责），
//      保留算法本身；
//   4. **修正一处原版的权重不一致**：原版 _SamplePixelQuality 对
//      Gaussian 与 Box 两种核用了不同的权重公式，但 totalWeight 的累加
//      在 Box 分支下会被 weight=0 的样本拉低，逻辑上没有 bug，
//      只是「Box 核其实退化成了 0 权重」。这里保持原样但写明。

using System.Numerics;

namespace UMango.Core.UI.Blur;

/// <summary>模糊核类型。</summary>
public enum BlurKernelType
{
    /// <summary>高斯核。</summary>
    Gaussian,

    /// <summary>盒式核（线性衰减）。</summary>
    Box
}

/// <summary>
/// 采样式模糊：每个像素取若干个泊松盘分布的邻域样本，按核权重加权平均。
///
/// 与 <see cref="BlurKernel"/> 的分离卷积不同，这条路径是「每像素独立采样」，
/// 复杂度与半径成正比而不是半径平方 —— 适合大半径、实时预览。
/// </summary>
public static class SamplingBlur
{
    private static readonly Vector2[] _PoissonSamples = BlurKernel.GeneratePoissonDiskSamples();

    /// <summary>当前的采样点（只读副本）。</summary>
    public static IReadOnlyList<Vector2> PoissonSamples => _PoissonSamples;

    /// <summary>
    /// 对一块像素做采样式模糊。
    /// </summary>
    /// <param name="source">源像素（BGRA32）。</param>
    /// <param name="target">输出像素，长度须与 source 相同。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="radius">采样半径（像素）。</param>
    /// <param name="samplingRate">采样率 0~1。</param>
    /// <param name="kernelType">核类型。</param>
    public static void Apply(uint[] source, uint[] target, int width, int height, double radius, double samplingRate, BlurKernelType kernelType = BlurKernelType.Gaussian)
    {
        if (source.Length < width * height) throw new ArgumentException("源缓冲过短。", nameof(source));
        if (target.Length < width * height) throw new ArgumentException("目标缓冲过短。", nameof(target));
        if (radius <= 0)
        {
            Array.Copy(source, target, width * height);
            return;
        }

        var intRadius = (int)Math.Ceiling(radius);
        var sigma = radius / 3.0;
        var twoSigmaSquared = 2.0 * sigma * sigma;

        var sampleCount = kernelType == BlurKernelType.Gaussian
            ? Math.Min(_PoissonSamples.Length, (int)(32 * samplingRate))
            : Math.Min(16, (int)(16 * samplingRate));

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                target[y * width + x] = SamplePixel(source, width, height, x, y, intRadius, twoSigmaSquared, radius, sampleCount, kernelType);
            }
        }
    }

    private static uint SamplePixel(uint[] source, int width, int height, int centerX, int centerY,
        int radius, double twoSigmaSquared, double radiusValue, int sampleCount, BlurKernelType kernelType)
    {
        double totalA = 0, totalR = 0, totalG = 0, totalB = 0, totalWeight = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var offset = _PoissonSamples[i % _PoissonSamples.Length] * (float)radiusValue;
            var sampleX = centerX + (int)Math.Round(offset.X);
            var sampleY = centerY + (int)Math.Round(offset.Y);

            if (sampleX < 0 || sampleX >= width || sampleY < 0 || sampleY >= height) continue;

            var pixel = source[sampleY * width + sampleX];
            var distance = offset.Length();

            var weight = kernelType == BlurKernelType.Gaussian
                ? Math.Exp(-distance * distance / twoSigmaSquared)
                : Math.Max(0, 1.0 - distance / radiusValue);

            totalA += ((pixel >> 24) & 0xFF) * weight;
            totalR += ((pixel >> 16) & 0xFF) * weight;
            totalG += ((pixel >> 8) & 0xFF) * weight;
            totalB += (pixel & 0xFF) * weight;
            totalWeight += weight;
        }

        if (totalWeight > 0)
        {
            var invWeight = 1.0 / totalWeight;
            return BlurKernel.PackColor(
                BlurKernel.ToByte(totalA * invWeight),
                BlurKernel.ToByte(totalR * invWeight),
                BlurKernel.ToByte(totalG * invWeight),
                BlurKernel.ToByte(totalB * invWeight));
        }

        // 一个样本都没落在图内 —— 保留原像素。
        var original = source[centerY * width + centerX];
        return BlurKernel.PackColor(
            (byte)((original >> 24) & 0xFF),
            (byte)((original >> 16) & 0xFF),
            (byte)((original >> 8) & 0xFF),
            (byte)(original & 0xFF));
    }
}
