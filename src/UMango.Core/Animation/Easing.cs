// 缓动函数。数学公式逐条取自 PCL-CE 的 PCL.Core/UI/Animation/Easings/ 与
// PCL.Core/Utils/EaseUtils.cs（Apache-2.0，允许复用）。
// 变更说明：由「每条一个类」改为「一个枚举 + 一张函数表」——
// 39 个各自只有一个方法的类属于重复代码，而本项目明确要求降低重复代码率；
// 数学表达式本身未作改动（包括 PCL 用 Math.Sin 实现的、非教科书常数的 Back 缓动）。

namespace UMango.Core.Animation;

/// <summary>缓动类型。命名与 PCL-CE 的类名一一对应，便于对照。</summary>
public enum EasingKind
{
    Linear,
    QuadIn, QuadOut, QuadInOut,
    CubicIn, CubicOut, CubicInOut,
    QuarticIn, QuarticOut, QuarticInOut,
    QuinticIn, QuinticOut, QuinticInOut,
    SineIn, SineOut, SineInOut,
    CircularIn, CircularOut, CircularInOut,
    ExponentialIn, ExponentialOut, ExponentialInOut,
    BackIn, BackOut, BackInOut,
    BounceIn, BounceOut, BounceInOut,
    ElasticIn, ElasticOut, ElasticInOut,
}

/// <summary>
/// 缓动基类。语义与 PCL-CE 一致：进度 &lt;=0 取 0、&gt;=1 取 1，中间交给 <see cref="EaseCore"/>。
/// </summary>
public abstract class Easing
{
    protected abstract double EaseCore(double progress);

    public double Ease(double progress) => progress switch
    {
        <= 0.0 => 0.0,
        >= 1.0 => 1.0,
        _ => EaseCore(progress)
    };

    /// <summary>按帧号求值（供逐帧驱动动画的实现使用）。</summary>
    public double Ease(int currentFrame, int totalFrames)
        => totalFrames <= 1 ? 1.0 : Ease((double)currentFrame / (totalFrames - 1));

    /// <summary>取指定类型的缓动实例（不可变、可共享）。</summary>
    public static Easing Of(EasingKind kind) => Table[kind];

    private static readonly Dictionary<EasingKind, Easing> Table = Build();

    private static Dictionary<EasingKind, Easing> Build()
    {
        var t = new Dictionary<EasingKind, Easing>();
        void Add(EasingKind k, Func<double, double> f) => t[k] = new FuncEasing(f);

        Add(EasingKind.Linear, p => p);

        Add(EasingKind.QuadIn, p => p * p);
        Add(EasingKind.QuadOut, p => p * (2 - p));
        Add(EasingKind.QuadInOut, p => p < 0.5 ? 2 * p * p : p * (4 - 2 * p) - 1);

        Add(EasingKind.CubicIn, p => p * p * p);
        Add(EasingKind.CubicOut, p => { var f = p - 1; return f * f * f + 1; });
        Add(EasingKind.CubicInOut, p =>
        {
            if (p < 0.5) return 4 * p * p * p;
            var f = 2 * (p - 1);
            return 0.5 * f * f * f + 1;
        });

        Add(EasingKind.QuarticIn, p => p * p * p * p);
        Add(EasingKind.QuarticOut, p => { var f = p - 1; var f2 = f * f; return -f2 * f2 + 1; });
        Add(EasingKind.QuarticInOut, p =>
        {
            if (p < 0.5) return 8 * p * p * p * p;
            var f = p - 1;
            return -8 * f * f * f * f + 1;
        });

        Add(EasingKind.QuinticIn, p => p * p * p * p * p);
        Add(EasingKind.QuinticOut, p => { var f = p - 1; var f2 = f * f; return f2 * f2 * f + 1; });
        Add(EasingKind.QuinticInOut, p =>
        {
            if (p < 0.5) return 16 * p * p * p * p * p;
            var f = p - 1;
            return 16 * f * f * f * f * f + 1;
        });

        Add(EasingKind.SineIn, p => Math.Sin((p - 1) * Math.PI / 2) + 1);
        Add(EasingKind.SineOut, p => Math.Sin(p * Math.PI / 2));
        Add(EasingKind.SineInOut, p => 0.5 * (1 - Math.Cos(p * Math.PI)));

        Add(EasingKind.CircularIn, p => 1 - Math.Sqrt(1 - p * p));
        Add(EasingKind.CircularOut, p => Math.Sqrt((2 - p) * p));
        Add(EasingKind.CircularInOut, p =>
        {
            if (p < 0.5) return 0.5 * (1 - Math.Sqrt(1 - 4 * p * p));
            var t = 2 * p;
            return 0.5 * (Math.Sqrt((3 - t) * (t - 1)) + 1);
        });

        Add(EasingKind.ExponentialIn, p => Math.Abs(p) < 1e-4 ? p : Math.Pow(2, 10 * (p - 1)));
        Add(EasingKind.ExponentialOut, p => Math.Abs(p - 1.0) < 1e-4 ? p : 1 - Math.Pow(2, -10 * p));
        Add(EasingKind.ExponentialInOut, p =>
        {
            if (Math.Abs(p) < 1e-4) return p;
            if (Math.Abs(p - 1.0) < 1e-4) return p;
            return p < 0.5 ? 0.5 * Math.Pow(2, 20 * p - 10) : 1 - 0.5 * Math.Pow(2, -20 * p + 10);
        });

        // PCL 的 Back 用 Math.Sin 而不是教科书的 1.70158 常数 —— 照抄，不要"修正"成教科书版本
        Add(EasingKind.BackIn, p => p * (p * p - Math.Sin(p * Math.PI)));
        Add(EasingKind.BackOut, p => { var q = 1 - p; return 1 - q * (q * q - Math.Sin(q * Math.PI)); });
        Add(EasingKind.BackInOut, p =>
        {
            if (p < 0.5)
            {
                var f = 2 * p;
                return 0.5 * f * (f * f - Math.Sin(f * Math.PI));
            }
            else
            {
                var f = 1 - (2 * p - 1);
                return 0.5 * (1 - f * (f * f - Math.Sin(f * Math.PI))) + 0.5;
            }
        });

        Add(EasingKind.BounceIn, p => 1 - Bounce(1 - p));
        Add(EasingKind.BounceOut, Bounce);
        Add(EasingKind.BounceInOut, p => p < 0.5
            ? 0.5 * (1 - Bounce(1 - p * 2))
            : 0.5 * Bounce(p * 2 - 1) + 0.5);

        Add(EasingKind.ElasticIn, p =>
            Math.Sin(ElasticPiTimes6Point5 * p) * Math.Exp(ElasticLn2Times10 * (p - 1d)));
        Add(EasingKind.ElasticOut, p =>
            Math.Sin(-ElasticPiTimes6Point5 * (p + 1d)) * Math.Exp(-ElasticLn2Times10 * p) + 1d);
        Add(EasingKind.ElasticInOut, p =>
        {
            if (p < 0.5)
            {
                var t = p * 2d;
                return 0.5d * Math.Sin(ElasticPiTimes6Point5 * t) * Math.Exp(ElasticLn2Times10 * (t - 1d));
            }
            else
            {
                var t = p * 2d - 1d;
                return 0.5d * Math.Sin(-ElasticPiTimes6Point5 * (t + 1d)) *
                       Math.Exp(-ElasticLn2Times10 * t) + 0.5d;
            }
        });

        return t;
    }

    // ---- 以下常量与分段函数取自 PCL.Core/Utils/EaseUtils.cs，数值原样保留 ----

    private const double BounceN1 = 7.5625;
    private const double BounceInvD1 = 0.3636363636363636;        // 1 / 2.75
    private const double BounceThreshold2 = 0.7272727272727273;   // 2 / 2.75
    private const double BounceThreshold3 = 0.9090909090909091;   // 2.5 / 2.75
    private const double BounceOffset1 = 0.5454545454545454;      // 1.5 / 2.75
    private const double BounceOffset2 = 0.8181818181818182;      // 2.25 / 2.75
    private const double BounceOffset3 = 0.9545454545454546;      // 2.625 / 2.75

    private const double ElasticLn2Times10 = 6.931471805599453;       // Math.Log(2) * 10
    private const double ElasticPiTimes6Point5 = 20.420352248333657;  // Math.PI * 6.5

    private static double Bounce(double progress) => progress switch
    {
        < BounceInvD1 => BounceN1 * progress * progress,
        < BounceThreshold2 => BounceN1 * Sq(progress - BounceOffset1) + 0.75,
        < BounceThreshold3 => BounceN1 * Sq(progress - BounceOffset2) + 0.9375,
        _ => BounceN1 * Sq(progress - BounceOffset3) + 0.984375
    };

    private static double Sq(double x) => x * x;

    /// <summary>把函数包装成缓动实例（避免为每个缓动写一个类）。</summary>
    private sealed class FuncEasing(Func<double, double> f) : Easing
    {
        protected override double EaseCore(double progress) => f(progress);
    }
}
