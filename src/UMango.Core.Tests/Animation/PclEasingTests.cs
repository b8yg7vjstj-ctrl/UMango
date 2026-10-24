using UMango.Core.Animation;

namespace UMango.Core.Tests.Animation;

/// <summary>
/// PCL 风格"档位缓动"的测试。
///
/// 这些缓动对应 PCL-CE `Plain Craft Launcher 2/Modules/Base/ModAnimation.cs` 里的
/// <c>AniEaseInFluent</c> / <c>AniEaseOutFluent</c> / <c>AniEaseInoutFluent</c> /
/// <c>AniEaseInCar</c> / <c>AniEaseOutCar</c> / <c>AniEaseInElastic</c> /
/// <c>AniEaseOutElastic</c> / <c>AniEaseOutFluentWithInitial</c>（受限许可目录 → 只对照语义）。
/// </summary>
public sealed class PclEasingTests
{
    [Theory]
    [InlineData(EasePower.Weak, 2d)]
    [InlineData(EasePower.Middle, 3d)]
    [InlineData(EasePower.Strong, 4d)]
    [InlineData(EasePower.ExtraStrong, 5d)]
    public void 平滑开始的幂次等于档位(EasePower power, double expected)
    {
        var easing = new FluentInEasing(power);

        Assert.Equal(expected, easing.Power, 6);
        // t=0.5 时 t^p = 0.5^p
        Assert.Equal(Math.Pow(0.5, expected), easing.Ease(0.5), 6);
    }

    [Theory]
    [InlineData(EasePower.Weak)]
    [InlineData(EasePower.Middle)]
    [InlineData(EasePower.Strong)]
    [InlineData(EasePower.ExtraStrong)]
    public void 平滑结束是平滑开始的镜像(EasePower power)
    {
        var inEasing = new FluentInEasing(power);
        var outEasing = new FluentOutEasing(power);

        for (var t = 0.05; t < 1.0; t += 0.05)
            Assert.Equal(1d - inEasing.Ease(1d - t), outEasing.Ease(t), 6);
    }

    [Fact]
    public void 缓动两端被夹到零和一()
    {
        var easing = new FluentInEasing(EasePower.Middle);

        Assert.Equal(0d, easing.Ease(0d), 6);
        Assert.Equal(1d, easing.Ease(1d), 6);
        Assert.Equal(0d, easing.Ease(-3d), 6);
        Assert.Equal(1d, easing.Ease(4d), 6);
    }

    [Fact]
    public void 平滑开始与结束在中点衔接()
    {
        var easing = new FluentInOutEasing(EasePower.Middle, middle: 0.5d);

        // 中点处 In 段刚好到 0.5，Out 段从 0.5 起 —— 连续。
        var before = easing.Ease(0.4999);
        var at = easing.Ease(0.5);
        var after = easing.Ease(0.5001);

        Assert.True(Math.Abs(at - before) < 0.01, $"中点前 {before}，中点 {at}");
        Assert.True(Math.Abs(after - at) < 0.01, $"中点 {at}，中点后 {after}");
    }

    [Fact]
    public void 平滑回弹在前段没有过冲()
    {
        // OutCar：前 0.3 走平滑开始（单调），后半才回弹。
        var easing = new CarOutEasing();

        var previous = easing.Ease(0d);
        for (var t = 0.02; t <= 0.3; t += 0.02)
        {
            var current = easing.Ease(t);
            Assert.True(current >= previous - 1e-9, $"t={t} 处出现回退：{previous} → {current}");
            previous = current;
        }
    }

    [Fact]
    public void 回弹平滑在后段出现过冲()
    {
        // OutCar 的后半段是回弹 → 必然超过 1 再回落。
        var easing = new CarOutEasing();
        var max = 0d;
        for (var t = 0d; t <= 1d; t += 0.005)
            max = Math.Max(max, easing.Ease(t));

        Assert.True(max > 1d, $"回弹段应超过 1，实际最大 {max}");
    }

    [Fact]
    public void 弹簧缓动的波形参数随档位变化()
    {
        Assert.Equal(6, new ElasticPowerEasing(EasePower.Weak).WaveParameter);
        Assert.Equal(7, new ElasticPowerEasing(EasePower.Middle).WaveParameter);
        Assert.Equal(8, new ElasticPowerEasing(EasePower.Strong).WaveParameter);
        Assert.Equal(9, new ElasticPowerEasing(EasePower.ExtraStrong).WaveParameter);
    }

    [Fact]
    public void 弹簧缓动两端被夹到零和一()
    {
        var easing = new ElasticPowerEasing(EasePower.Middle, isIn: true);

        // PCL 原版在 t=0 处 cos 项不为 1，曲线起点会跳变 —— UMango 夹紧。
        Assert.Equal(0d, easing.Ease(0d), 6);
        Assert.Equal(1d, easing.Ease(1d), 6);
    }

    [Fact]
    public void 弹簧结束是弹簧开始的镜像()
    {
        var inEasing = new ElasticPowerEasing(EasePower.Middle, isIn: true);
        var outEasing = new ElasticPowerEasing(EasePower.Middle, isIn: false);

        for (var t = 0.05; t < 1.0; t += 0.05)
            Assert.Equal(1d - inEasing.Ease(1d - t), outEasing.Ease(t), 6);
    }

    [Fact]
    public void 初速度缓动在初速度等于平均速度时退化为线性()
    {
        // 平均速度 = 总距离 / 总时长；初速度等于它 → α = 0 → 线性。
        var easing = new FluentOutWithInitialEasing(
            initialPixelPerSecond: 100d,
            totalSecond: 1d,
            totalDistance: 100d);

        Assert.Equal(0d, easing.Alpha, 9);
        Assert.Equal(0.5d, easing.Ease(0.5d), 6);
    }

    [Fact]
    public void 初速度缓动在初速度低于平均速度时也退化为线性()
    {
        var easing = new FluentOutWithInitialEasing(
            initialPixelPerSecond: 10d,
            totalSecond: 1d,
            totalDistance: 100d);

        Assert.Equal(0d, easing.Alpha, 9);
        Assert.Equal(0.5d, easing.Ease(0.5d), 6);
    }

    [Fact]
    public void 初速度缓动在初速度高于平均速度时先快后慢()
    {
        var easing = new FluentOutWithInitialEasing(
            initialPixelPerSecond: 400d,
            totalSecond: 1d,
            totalDistance: 100d);

        Assert.True(easing.Alpha > 0d);
        // 初速更快 → 早期进度应领先于线性。
        Assert.True(easing.Ease(0.25) > 0.25, $"t=0.25 处 {easing.Ease(0.25)}");
        Assert.Equal(1d, easing.Ease(1d), 6);
    }

    [Fact]
    public void 初速度缓动在零总时长或零总距离时报错()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FluentOutWithInitialEasing(100d, 0d, 100d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FluentOutWithInitialEasing(100d, 1d, 0d));
    }
}
