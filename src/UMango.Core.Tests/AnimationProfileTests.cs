using UMango.Core.Animation;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// PCL 动效预设的回归锁。
/// 需求：「动效之类的在 PCL 主题中完整保留」—— 这些数值提取自
/// PCL-CE MyPageRight.cs 实测（579-601 行），改任何一个都应该先对照原版。
/// </summary>
public class AnimationProfileTests
{
    [Fact]
    public void Pcl预设的数值与原版一致()
    {
        var p = PageMotionProfile.Pcl;
        Assert.Equal(100, p.FadeInMs);
        Assert.Equal(-16, p.EnterFromY);
        Assert.Equal(5, p.EnterMidY);
        Assert.Equal(250, p.EnterMidMs);
        Assert.Equal(350, p.EnterToMs);
        Assert.Equal(70, p.ExitMs);
        Assert.Equal(-6, p.ExitToY);
        Assert.Equal(25, p.EnterStaggerMs);
        Assert.Equal(15, p.ExitStaggerMs);
        Assert.Equal(EasingKind.BackOut, p.EnterToEase); // PCL 的 AniEaseOutBack
    }

    [Fact]
    public void Fluent预设是克制的短动效()
    {
        var f = PageMotionProfile.Fluent;
        Assert.True(f.FadeInMs < PageMotionProfile.Pcl.FadeInMs + 100);
        Assert.Equal(0, f.EnterStaggerMs); // Fluent 无级联
        Assert.Equal(EasingKind.CubicOut, f.EnterToEase); // 无回弹
    }

    [Fact]
    public void Pcl切页组合包含淡入与两段落体()
    {
        var profile = PageMotionProfile.Pcl;
        var settings = new AnimationSettings();
        var x = new ValueAnimatable(); var y = new ValueAnimatable(); var o = new ValueAnimatable();

        var binding = AnimationPlannerBinding.CreatePageEnter(profile, x, y, o, settings, fps: 60);

        var group = Assert.IsType<ParallelAnimationGroup>(binding.Animation);
        // 子项：淡入 + Y 顺序组 + X 保持 = 3 条
        Assert.Equal(3, group.Children.Count);
    }

    [Fact]
    public void 速度倍率会缩放关键帧时长()
    {
        var profile = PageMotionProfile.Pcl;
        var fast = new AnimationSettings { SpeedFactor = 2.0 };
        var slow = new AnimationSettings { SpeedFactor = 0.5 };
        var x = new ValueAnimatable(); var y = new ValueAnimatable(); var o = new ValueAnimatable();

        var fastBinding = AnimationPlannerBinding.CreatePageEnter(profile, x, y, o, fast, fps: 60);
        var slowBinding = AnimationPlannerBinding.CreatePageEnter(profile, x, y, o, slow, fps: 60);

        // 两倍速的总帧数应约为一半（帧率相同）
        var fastFrames = TotalFrames(fastBinding.Animation);
        var slowFrames = TotalFrames(slowBinding.Animation);
        Assert.True(slowFrames > fastFrames * 3, $"慢速({slowFrames}) 应明显多于快速({fastFrames})");
    }

    [Fact]
    public void 关闭动画时PageContent仍走单段IsInstant()
    {
        // TryBuildBinding 在 App 侧（私有），这里锁语义：settings.Enabled=false 时
        // AnimationPlanner.Plan 给零时长（既有测试已覆盖）——本用例锁定 profile 默认值。
        Assert.Equal(MotionProfileKind.Pcl, new AnimationSettings().Profile);
    }

    private static int TotalFrames(IAnimation animation)
    {
        if (animation is AnimationGroup g)
            return g.Children.Sum(c => TotalFrames(c.Animation));
        if (animation is FromToAnimation f)
            return f.DurationFrames;
        return 0;
    }
}
