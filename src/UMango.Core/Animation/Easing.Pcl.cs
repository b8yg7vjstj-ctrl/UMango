// SPDX-License-Identifier: Apache-2.0
//
// PCL 风格的"档位缓动"集合。
//
// 数学表达式的来源分两类，来源不同、许可不同，这里分别注明：
//   · Fluent / Car / Elastic(带档位) / OutFluentWithInitial：
//     取自 PCL-CE `Plain Craft Launcher 2/Modules/Base/ModAnimation.cs`
//     （**受限许可目录**）—— 按《PCL 分发有限许可》，此处**只借鉴设计语义与
//     公开的数学公式**，不复制其代码实现；本文件为独立重写。
//   · 与 PCL-CE `PCL.Core/UI/Animation/Easings/BackEaseWithPower*.cs`（Apache-2.0）
//     同族的思路：回弹强度由"档位"决定，而不是写死常数。
//
// 为什么要有这一层：PCL 的 UI 动效大量使用"档位"（Weak/Middle/Strong/ExtraStrong）
// 概念 —— 同一个缓动家族配不同档位，就得到一组观感相近但力度不同的动效。
// UMango 现有的 <see cref="EasingKind"/> 枚举把 39 个缓动压平成"一个枚举 + 一张表"，
// 无法表达"带参数的档位缓动"，所以这里补一组**类**（可带参数）。

namespace UMango.Core.Animation;

/// <summary>
/// 平滑开始（幂缓动 In）：<c>t^p</c>。
///
/// 对应 PCL 的 <c>AniEaseInFluent</c>。
/// </summary>
public sealed class FluentInEasing : Easing
{
    private readonly double _power;

    public FluentInEasing(EasePower power = EasePower.Middle) => _power = (double)power;

    /// <summary>幂次（档位数值本身）。</summary>
    public double Power => _power;

    protected override double EaseCore(double progress) => Math.Pow(progress, _power);
}

/// <summary>
/// 平滑结束（幂缓动 Out）：<c>1-(1-t)^p</c>。
///
/// 对应 PCL 的 <c>AniEaseOutFluent</c>。
/// </summary>
public sealed class FluentOutEasing : Easing
{
    private readonly double _power;

    public FluentOutEasing(EasePower power = EasePower.Middle) => _power = (double)power;

    /// <summary>幂次（档位数值本身）。</summary>
    public double Power => _power;

    protected override double EaseCore(double progress) => 1d - Math.Pow(1d - progress, _power);
}

/// <summary>
/// 平滑开始与结束：前 <c>middle</c> 走 In，后半走 Out。
///
/// 对应 PCL 的 <c>AniEaseInoutFluent</c>。
/// </summary>
public sealed class FluentInOutEasing : Easing
{
    private readonly CombinedEasing _ease;

    public FluentInOutEasing(EasePower power = EasePower.Middle, double middle = 0.5d)
        => _ease = new CombinedEasing(new FluentInEasing(power), new FluentOutEasing(power), middle);

    protected override double EaseCore(double progress) => _ease.Ease(progress);
}

/// <summary>
/// 平滑-回弹：前 <c>middle</c> 走平滑开始，后半走回弹结束。
///
/// 对应 PCL 的 <c>AniEaseOutCar</c>（默认 middle = 0.3）。
/// </summary>
public sealed class CarOutEasing : Easing
{
    private readonly CombinedEasing _ease;

    public CarOutEasing(double middle = 0.3d, EasePower power = EasePower.Middle)
        => _ease = new CombinedEasing(new FluentInEasing(power), new BackEaseWithPowerEasing(power, isIn: false), middle);

    protected override double EaseCore(double progress) => _ease.Ease(progress);
}

/// <summary>
/// 回弹-平滑：前 <c>middle</c> 走回弹开始，后半走平滑结束。
///
/// 对应 PCL 的 <c>AniEaseInCar</c>（默认 middle = 0.7）。
/// </summary>
public sealed class CarInEasing : Easing
{
    private readonly CombinedEasing _ease;

    public CarInEasing(double middle = 0.7d, EasePower power = EasePower.Middle)
        => _ease = new CombinedEasing(new BackEaseWithPowerEasing(power, isIn: true), new FluentOutEasing(power), middle);

    protected override double EaseCore(double progress) => _ease.Ease(progress);
}

/// <summary>
/// 弹簧（带档位）。
///
/// 对应 PCL 的 <c>AniEaseInElastic</c> / <c>AniEaseOutElastic</c>：
/// 指数由档位决定（<c>p = 档位 + 4</c>，即 6~9），
/// 波形用 <c>cos((p-3.5)·π·(1-t)^1.5)</c>。
///
/// UMango 与 PCL 的公式一致，并依赖基类 <see cref="Easing"/> 把两端夹到 0/1
/// （所以第一帧一定落在起点值，元素不会在动画开始瞬间闪一下）。
/// </summary>
public sealed class ElasticPowerEasing : Easing
{
    private readonly int _p;
    private readonly bool _isIn;

    public ElasticPowerEasing(EasePower power = EasePower.Middle, bool isIn = false)
    {
        _p = (int)power + 4;
        _isIn = isIn;
    }

    /// <summary>波形参数（6~9）。</summary>
    public int WaveParameter => _p;

    protected override double EaseCore(double progress)
    {
        var t = _isIn ? progress : 1d - progress;
        var value = Math.Pow(t, (_p - 1) * 0.25d) * Math.Cos((_p - 3.5d) * Math.PI * Math.Pow(1d - t, 1.5d));
        return _isIn ? value : 1d - value;
    }
}

/// <summary>
/// 以特定初速度开始的平滑结束。
///
/// 对应 PCL 的 <c>AniEaseOutFluentWithInitial</c>：
/// 用「初速度 / 平均速度」推导出有理式曲线 <c>(α+1)p / (1+αp)</c>，
/// 其中 <c>α = 初速度·总时长/总距离 - 1</c>。
/// 初速度低于平均速度时（<c>α &lt; 0</c>）退化为线性。
/// </summary>
public sealed class FluentOutWithInitialEasing : Easing
{
    private readonly double _alpha;

    public FluentOutWithInitialEasing(double initialPixelPerSecond, double totalSecond, double totalDistance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalSecond, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalDistance, 0d);

        // 归一化初速度：初速度 × 总时长 / 总距离。
        var normalized = initialPixelPerSecond * totalSecond / totalDistance;
        _alpha = Math.Max(0d, normalized - 1d);
    }

    /// <summary>曲线参数 α（≥0，为 0 表示退化为线性）。</summary>
    public double Alpha => _alpha;

    protected override double EaseCore(double progress)
    {
        if (_alpha == 0d) return progress;
        return (_alpha + 1d) * progress / (1d + _alpha * progress);
    }
}
