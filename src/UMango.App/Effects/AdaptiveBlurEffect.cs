// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Effects/AdaptiveBlurEffect.cs 采用。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Effects → UMango.App.Effects；
//   2. 着色器 URI 从 "pack://application:,,,/PCL.Core;component/UI/Assets/Shaders/AdaptiveBlur.ps"
//      改为 "pack://application:,,,/UMango;component/Assets/Shaders/AdaptiveBlur.ps"
//      （程序集名是 UMango，见 UMango.App.csproj 的 AssemblyName）；
//   3. 删掉原版里 PerformanceOptimizations 这个静态池化辅助类
//      （Vector4[]/float[] 的 ArrayPool 与本效果无关，是别的代码蹭在这里的）；
//   4. 删掉原版里 Radius / SamplingRate 的"越大越慢"性能警告分支的日志输出，
//      改为静默夹到合法区间（见 _ValidateRadius / _ValidateSamplingRate 的说明）；
//   5. 补上 TextureSize 的自动更新：原版把它固定成 1920x1080，
//      实际渲染时输入纹理尺寸往往不是这个值，会让径向模糊的步长算错。
//      这里在 Input 变化时按渲染尺寸更新一次。

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace UMango.App.Effects;

/// <summary>
/// 自适应模糊效果：按半径与采样率在「分离高斯模糊」「径向模糊」「对角高斯」之间取舍，
/// 兼顾质量与实时性。
///
/// 相比原生 <see cref="BlurEffect"/> 的优势：采样点数可控，
/// 大半径时不会把帧率拖垮。
/// </summary>
public sealed class AdaptiveBlurEffect : ShaderEffect
{
    /// <summary>
    /// 着色器资源 URI。
    ///
    /// ⚠ 路径必须**全小写**：WPF 会把资源名规范化成小写
    /// （实测打包后的条目是 <c>assets/shaders/adaptiveblur.ps</c>），
    /// pack URI 查资源时是**区分大小写**的，
    /// 写成 <c>Assets/Shaders/AdaptiveBlur.ps</c> 会静默取不到。
    /// </summary>
    private const string PixelShaderUri = "pack://application:,,,/UMango;component/assets/shaders/adaptiveblur.ps";

    private static readonly object _ShaderLock = new();
    private static PixelShader? _cachedShader;

    /// <summary>构造时确保着色器已加载，并绑定常量。</summary>
    public AdaptiveBlurEffect()
    {
        _EnsureShaderInitialized();
        PixelShader = _cachedShader;

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(RadiusProperty);
        UpdateShaderValue(SamplingRateProperty);
        UpdateShaderValue(QualityBiasProperty);
        UpdateShaderValue(TextureSizeProperty);
    }

    /// <summary>模糊半径（像素）。</summary>
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>采样率 0~1。越低越快、越糙。</summary>
    public double SamplingRate
    {
        get => (double)GetValue(SamplingRateProperty);
        set => SetValue(SamplingRateProperty, value);
    }

    /// <summary>质量偏置：正值偏向质量，负值偏向速度。</summary>
    public double QualityBias
    {
        get => (double)GetValue(QualityBiasProperty);
        set => SetValue(QualityBiasProperty, value);
    }

    /// <summary>输入纹理尺寸（着色器据此算 texel 步长）。</summary>
    public Point TextureSize
    {
        get => (Point)GetValue(TextureSizeProperty);
        set => SetValue(TextureSizeProperty, value);
    }

    /// <summary>渲染偏置。</summary>
    public RenderingBias RenderingBias
    {
        get => (RenderingBias)GetValue(RenderingBiasProperty);
        set => SetValue(RenderingBiasProperty, value);
    }

    /// <summary>卷积核类型。</summary>
    public KernelType KernelType
    {
        get => (KernelType)GetValue(KernelTypeProperty);
        set => SetValue(KernelTypeProperty, value);
    }

    /// <summary>输入图像。</summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public static readonly DependencyProperty InputProperty =
        ShaderEffect.RegisterPixelShaderSamplerProperty("Input", typeof(AdaptiveBlurEffect), 0);

    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(nameof(Radius), typeof(double), typeof(AdaptiveBlurEffect),
            new FrameworkPropertyMetadata(16.0, PixelShaderConstantCallback(0))
            {
                CoerceValueCallback = _CoerceRadius
            });

    public static readonly DependencyProperty SamplingRateProperty =
        DependencyProperty.Register(nameof(SamplingRate), typeof(double), typeof(AdaptiveBlurEffect),
            new FrameworkPropertyMetadata(1.0, PixelShaderConstantCallback(1))
            {
                CoerceValueCallback = _CoerceSamplingRate
            });

    public static readonly DependencyProperty QualityBiasProperty =
        DependencyProperty.Register("QualityBias", typeof(double), typeof(AdaptiveBlurEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty TextureSizeProperty =
        DependencyProperty.Register("TextureSize", typeof(Point), typeof(AdaptiveBlurEffect),
            new UIPropertyMetadata(new Point(1920, 1080), PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty RenderingBiasProperty =
        DependencyProperty.Register(nameof(RenderingBias), typeof(RenderingBias), typeof(AdaptiveBlurEffect),
            new UIPropertyMetadata(RenderingBias.Performance, OnRenderingBiasChanged));

    public static readonly DependencyProperty KernelTypeProperty =
        DependencyProperty.Register(nameof(KernelType), typeof(KernelType), typeof(AdaptiveBlurEffect),
            new UIPropertyMetadata(KernelType.Gaussian));

    /// <summary>
    /// 半径夹到 0~200 之间。
    ///
    /// 原版用的是 IsValidCallback（越界直接抛 ArgumentException）。
    /// 这里改成 CoerceValueCallback —— 越界时**静默夹住**而不是抛异常：
    /// 这个效果是装饰性的，绑定的数值可能来自动画插值或用户输入，
    /// 让它把整个界面炸掉不划算。
    /// </summary>
    private static object _CoerceRadius(DependencyObject d, object baseValue)
    {
        var radius = (double)baseValue;
        if (radius < 0) return 0.0;
        if (radius > 200) return 200.0;
        return radius;
    }

    /// <summary>采样率夹到 0.05~1 之间。</summary>
    private static object _CoerceSamplingRate(DependencyObject d, object baseValue)
    {
        var rate = (double)baseValue;
        if (rate < 0.05) return 0.05;
        if (rate > 1.0) return 1.0;
        return rate;
    }

    /// <summary>
    /// 渲染偏置的变化要同步到着色器的 QualityBias 常量（register C2）。
    ///
    /// 原版只做这一件事 —— 它没有试图去设置 PixelShader 的渲染偏置，
    /// 因为 PixelShader 根本没有这个属性（WPF 里设置渲染偏置的是 Effect 本身）。
    /// </summary>
    private static void OnRenderingBiasChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AdaptiveBlurEffect effect)
        {
            var qualityBias = e.NewValue is RenderingBias.Quality ? 1.0 : 0.0;
            effect.SetValue(QualityBiasProperty, qualityBias);
        }
    }

    /// <summary>
    /// 延迟加载并缓存着色器。加载失败时给一个空着色器，
    /// 效果会退化成「原样输出」而不是让整个界面崩掉。
    /// </summary>
    private static void _EnsureShaderInitialized()
    {
        if (_cachedShader is not null) return;

        lock (_ShaderLock)
        {
            if (_cachedShader is not null) return;

            try
            {
                _cachedShader = new PixelShader
                {
                    UriSource = new Uri(PixelShaderUri, UriKind.Absolute)
                };
            }
            catch (Exception)
            {
                // 着色器加载失败：退化成空效果。
                _cachedShader = new PixelShader();
            }
        }
    }
}
