using PCL;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// PCL-CE 原版缓动数学的回归锁（ModAnimation.cs 逐字采用后的行为验证）。
/// 数学定义来自原版 AniEase 类，改任何一行都应先对照 PCL 原版。
/// </summary>
public class PclAnimationMathTests
{
    [Fact]
    public void AniEaseOutFluent_数学为1减负幂()
    {
        // 原版：1 - (1-t)^p；Weak=2 → t=0.5 时 1-0.25=0.75
        var ease = new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak);
        Assert.Equal(0.75, ease.GetValue(0.5), precision: 10);
        Assert.Equal(0.0, ease.GetValue(0.0), precision: 10);
        Assert.Equal(1.0, ease.GetValue(1.0), precision: 10);
    }

    [Fact]
    public void AniEasePower_枚举值与原版一致()
    {
        // 原版 ModAnimation.cs:1200-1206：Weak=2, Middle=3, Strong=4, ExtraStrong=5
        Assert.Equal(2, (int)ModAnimation.AniEasePower.Weak);
        Assert.Equal(3, (int)ModAnimation.AniEasePower.Middle);
        Assert.Equal(4, (int)ModAnimation.AniEasePower.Strong);
        Assert.Equal(5, (int)ModAnimation.AniEasePower.ExtraStrong);
    }

    [Fact]
    public void AniEaseOutBack_端点收敛且有过冲()
    {
        var ease = new ModAnimation.AniEaseOutBack();
        Assert.Equal(0.0, ease.GetValue(0.0), precision: 10);
        Assert.Equal(1.0, ease.GetValue(1.0), precision: 10);
        // BackOut 的特征：中途超过 1（过冲）再回落
        var max = Enumerable.Range(1, 99).Select(i => ease.GetValue(i / 100.0)).Max();
        Assert.True(max > 1.0, $"OutBack 应有过冲（最大值 {max}）");
    }

    [Fact]
    public void AniControlEnabled_默认为0()
    {
        Assert.Equal(0, ModAnimation.AniControlEnabled);
    }

    [Fact]
    public void AniData_默认缓动为线性()
    {
        // 原版：Aa* 工厂在 ease=null 时用 AniEaseLinear（ModAnimation.cs:1166）
        var d = ModAnimation.AaTranslateY(new object(), 5d, 250);
        Assert.IsType<ModAnimation.AniEaseLinear>(d.ease);
        Assert.Equal(250, d.timeTotal);
        Assert.Equal(0, d.timeFinished); // delay=0 时初始为 0（delay 经负数表示）
    }
}
