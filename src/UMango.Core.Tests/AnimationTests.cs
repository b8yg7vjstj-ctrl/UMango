using UMango.Core.Animation;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 动画层测试。
///
/// 两个重点：
///   1. **方向镜像**（需求原文：列表 = 垂直进入 vs PCL 的水平；对话框 = 右滑 vs PCL 的左滑）
///      —— 两种模式都必须存在且可全局切换；
///   2. **缓动公式与 PCL-CE 一致** —— 公式是从 PCL.Core/UI/Animation/Easings 逐条搬过来的，
///      这里用可手算的定点值校验，防止"看着像但数值不对"。
/// </summary>
public class AnimationTests
{
    // ================= 缓动 =================

    [Fact]
    public void 全部缓动类型都能取到实例()
    {
        foreach (var kind in Enum.GetValues<EasingKind>())
        {
            var e = Easing.Of(kind);
            Assert.NotNull(e);
            // 端点语义与 PCL 一致
            Assert.Equal(0.0, e.Ease(0.0));
            Assert.Equal(1.0, e.Ease(1.0));
        }
    }

    [Fact]
    public void 端点外的输入被夹住而不是外推()
    {
        var e = Easing.Of(EasingKind.CubicIn);
        Assert.Equal(0.0, e.Ease(-5));
        Assert.Equal(1.0, e.Ease(7));
    }

    [Theory]
    [InlineData(EasingKind.Linear, 0.5, 0.5)]
    [InlineData(EasingKind.QuadIn, 0.5, 0.25)]           // 0.5²
    [InlineData(EasingKind.CubicIn, 0.5, 0.125)]         // 0.5³
    [InlineData(EasingKind.CubicOut, 0.5, 0.875)]        // (-0.5)³ + 1
    [InlineData(EasingKind.QuadOut, 0.5, 0.75)]          // 0.5 * (2 - 0.5)
    [InlineData(EasingKind.SineOut, 0.5, 0.7071067811865476)]  // sin(π/4)
    [InlineData(EasingKind.CircularOut, 0.5, 0.8660254037844386)] // sqrt(1.5*0.5)
    public void 缓动公式与PCL一致_定点校验(EasingKind kind, double p, double expected)
        => Assert.Equal(expected, Easing.Of(kind).Ease(p), 9);

    [Fact]
    public void Back缓动用的是PCL的Sin实现而不是教科书常数()
    {
        // PCL: BackIn(p) = p * (p² - sin(pπ))
        // 教科书版会用 1.70158 常数，两者在 0.5 处差别很大：
        //   PCL:      0.5 * (0.25 - 1)        = -0.375
        //   教科书:   0.5 * (0.25*(2.70158)-1) ≈ -0.0877
        // 这里明确断言 PCL 的取值，防止有人"顺手改成教科书版"
        Assert.Equal(-0.375, Easing.Of(EasingKind.BackIn).Ease(0.5), 9);
    }

    [Fact]
    public void Bounce缓动用的是PCL的预计算常数()
    {
        // PCL: 0.5 落在 [1/2.75, 2/2.75) 段 → 7.5625 * (0.5 - 1.5/2.75)² + 0.75
        //   0.5 - 0.5454545454545454 = -0.0454545454545454
        //   平方 = 0.002066115702479339，乘 7.5625 = 0.015625，加 0.75 = 0.765625
        Assert.Equal(0.765625, Easing.Of(EasingKind.BounceOut).Ease(0.5), 9);
    }

    [Fact]
    public void 缓动单调性抽查_Out系应当先快后慢()
    {
        // Out 系的特征：前半程走过的距离大于一半
        foreach (var kind in new[] { EasingKind.QuadOut, EasingKind.CubicOut, EasingKind.QuarticOut })
        {
            var half = Easing.Of(kind).Ease(0.5);
            Assert.True(half > 0.5, $"{kind} 在 0.5 处应已过半，实际 {half}");
        }
    }

    // ================= 方向镜像（需求核心） =================

    [Fact]
    public void 列表项在镜像模式下垂直进入_在PCL模式下水平进入()
    {
        var mirrored = AnimationPlanner.Plan(ElementMotion.ListItem,
            new AnimationSettings { Direction = AnimationDirectionMode.Mirrored });
        var pcl = AnimationPlanner.Plan(ElementMotion.ListItem,
            new AnimationSettings { Direction = AnimationDirectionMode.PclCompatible });

        // UMango：垂直
        Assert.True(mirrored.IsVertical, "镜像模式下列表项应垂直进入");
        Assert.Equal(0, mirrored.HorizontalDistance, 6);

        // PCL：水平
        Assert.True(pcl.IsHorizontal, "PCL 模式下列表项应水平进入");
        Assert.Equal(0, pcl.VerticalDistance, 6);
    }

    [Fact]
    public void 对话框在镜像模式下向右滑入_在PCL模式下向左滑入()
    {
        var mirrored = AnimationPlanner.Plan(ElementMotion.Dialog,
            new AnimationSettings { Direction = AnimationDirectionMode.Mirrored });
        var pcl = AnimationPlanner.Plan(ElementMotion.Dialog,
            new AnimationSettings { Direction = AnimationDirectionMode.PclCompatible });

        // 「向右滑入」= 起始在左侧（负 X）→ 0，视觉上整体向右移动
        Assert.True(mirrored.FromOffsetX < 0, $"镜像模式对话框应从左侧进入，实际 X={mirrored.FromOffsetX}");
        Assert.Equal(0, mirrored.ToOffsetX);

        // 「向左滑入」= 起始在右侧（正 X）→ 0，视觉上整体向左移动
        Assert.True(pcl.FromOffsetX > 0, $"PCL 模式对话框应从右侧进入，实际 X={pcl.FromOffsetX}");
        Assert.Equal(0, pcl.ToOffsetX);

        // 两者必须是真正的镜像：位移量相同、方向相反
        Assert.Equal(mirrored.HorizontalDistance, pcl.HorizontalDistance, 6);
    }

    [Fact]
    public void 默认模式是UMango自有矢量而不是PCL矢量()
    {
        // 需求要求"故意镜像"，所以默认必须是镜像模式 —— 否则就成了"复刻 PCL"而不是"UMango"
        var settings = new AnimationSettings();
        Assert.Equal(AnimationDirectionMode.Mirrored, settings.Direction);
        Assert.True(AnimationPlanner.Plan(ElementMotion.ListItem, settings).IsVertical);
    }

    [Fact]
    public void 全局开关能一键翻转所有动画矢量()
    {
        var s = new AnimationSettings { Direction = AnimationDirectionMode.Mirrored };
        var listBefore = AnimationPlanner.Plan(ElementMotion.ListItem, s);
        var dialogBefore = AnimationPlanner.Plan(ElementMotion.Dialog, s);

        // 切到 PCL 模式：两个元素的方向都应翻转
        s.Direction = AnimationDirectionMode.PclCompatible;
        var listAfter = AnimationPlanner.Plan(ElementMotion.ListItem, s);
        var dialogAfter = AnimationPlanner.Plan(ElementMotion.Dialog, s);

        Assert.True(listBefore.IsVertical && listAfter.IsHorizontal);
        Assert.True(dialogBefore.FromOffsetX < 0 && dialogAfter.FromOffsetX > 0);
    }

    // ================= 开关与速度 =================

    [Fact]
    public void 关掉动画时时长归零且直接显示终态()
    {
        var spec = AnimationPlanner.Plan(ElementMotion.ListItem,
            new AnimationSettings { Enabled = false });

        Assert.True(spec.IsInstant);
        Assert.Equal(0, spec.DurationMs);
        Assert.Equal(0, spec.FromOffsetX);
        Assert.Equal(0, spec.FromOffsetY);
        Assert.Equal(1.0, spec.FromOpacity);   // 不能留个透明起帧，否则关掉动画后元素是隐形的
        Assert.Equal(1.0, spec.ToOpacity);
    }

    [Fact]
    public void 速度倍率影响时长()
    {
        var normal = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings { SpeedFactor = 1.0 });
        var fast = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings { SpeedFactor = 2.0 });
        var slow = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings { SpeedFactor = 0.5 });

        Assert.True(fast.DurationMs < normal.DurationMs, "加速应缩短时长");
        Assert.True(slow.DurationMs > normal.DurationMs, "减速应延长时长");
        Assert.Equal(normal.DurationMs / 2, fast.DurationMs);
    }

    [Fact]
    public void 速度倍率被夹住_不会出现零或负时长()
    {
        var s = new AnimationSettings { SpeedFactor = 0 };
        Assert.True(s.EffectiveSpeedFactor > 0);
        Assert.True(AnimationPlanner.Plan(ElementMotion.ListItem, s).DurationMs > 0);

        var neg = new AnimationSettings { SpeedFactor = -3 };
        Assert.True(AnimationPlanner.Plan(ElementMotion.ListItem, neg).DurationMs > 0);
    }

    [Fact]
    public void 默认值对齐PCL的语义()
    {
        var s = new AnimationSettings();
        Assert.True(s.Enabled);
        Assert.Equal(1.0, s.SpeedFactor);
        // PCL-CE 的 Config.System.AnimationFpsLimit 默认是 59（UiAniFPS）
        Assert.Equal(59, s.FpsLimit);
    }

    [Fact]
    public void 起始偏移与动画规格一致()
    {
        var s = new AnimationSettings { Direction = AnimationDirectionMode.Mirrored };
        var (x, y) = AnimationPlanner.StartOffset(ElementMotion.ListItem, s);
        var spec = AnimationPlanner.Plan(ElementMotion.ListItem, s);

        Assert.Equal(spec.FromOffsetX, x);
        Assert.Equal(spec.FromOffsetY, y);
    }

    [Fact]
    public void 对话框位移量大于列表项_视觉层级不同()
    {
        var list = AnimationPlanner.Plan(ElementMotion.ListItem, new AnimationSettings());
        var dialog = AnimationPlanner.Plan(ElementMotion.Dialog, new AnimationSettings());
        Assert.True(dialog.VerticalDistance + dialog.HorizontalDistance
                    > list.VerticalDistance + list.HorizontalDistance);
    }

    [Fact]
    public void 克隆设置不会共享引用()
    {
        var a = new AnimationSettings();
        var b = a.Clone();
        b.Direction = AnimationDirectionMode.PclCompatible;
        Assert.Equal(AnimationDirectionMode.Mirrored, a.Direction);
    }
}
