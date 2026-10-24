// SPDX-License-Identifier: Apache-2.0
//
// 级联（stagger）编排的测试 —— 对应 PCL-CE 的
//   · Plain Craft Launcher 2/Controls/MyPageRight.cs:552-600（入场）/ :622-650（退场）
//   · Plain Craft Launcher 2/Controls/MyPageLeft.cs:52-95（入场）/ :113-131（退场）
// （受限许可目录，只借鉴设计语义与公开数值）。
//
// 纯数据 → 可以在 macOS 上直接断言每一项的延迟与补间。

using UMango.Core.Animation;
using Xunit;

namespace UMango.Core.Tests.Animation;

public class StaggerPlannerTests
{
    // ---------- 入场：右栏（纵向，恒定 25ms 步进） ----------

    [Fact]
    public void 右栏入场第一个子项延迟为零()
    {
        var plans = StaggerPlanner.Build(3, StaggerAxis.Vertical, StaggerPhase.Enter);
        Assert.Equal(0, plans[0].DelayMs);
    }

    [Fact]
    public void 右栏入场延迟按恒定25毫秒递增()
    {
        var plans = StaggerPlanner.Build(4, StaggerAxis.Vertical, StaggerPhase.Enter);
        Assert.Equal([0, 25, 50, 75], plans.Select(p => p.DelayMs));
    }

    [Fact]
    public void 右栏入场初始偏移是负16()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter);
        Assert.Equal(-16d, plans[0].FromOffset);
    }

    [Fact]
    public void 右栏入场初始不透明度是零()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter);
        Assert.Equal(0d, plans[0].FromOpacity);
        Assert.Equal(1d, plans[0].OpacityTarget);
    }

    [Fact]
    public void 右栏入场是三条补间()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter);
        Assert.Equal(3, plans[0].Specs.Count);
    }

    [Fact]
    public void 右栏入场的三条补间共享同一个延迟()
    {
        // PCL 的三条补间用同一个 delay（该控件的级联延迟），不是各自独立。
        var plans = StaggerPlanner.Build(3, StaggerAxis.Vertical, StaggerPhase.Enter);
        foreach (var spec in plans[2].Specs) Assert.Equal(plans[2].DelayMs, spec.DelayMs);
    }

    [Fact]
    public void 右栏入场的位移量是增量5和11()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter);
        var translates = plans[0].Specs.Where(s => s.Channel == TweenChannel.TranslateY).ToList();
        Assert.Equal(2, translates.Count);
        Assert.Equal(5d, translates[0].Value);
        Assert.Equal(11d, translates[1].Value);
    }

    [Fact]
    public void 右栏入场时长是100与250与350毫秒()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter);
        var fade = plans[0].Specs[0];
        var mid = plans[0].Specs[1];
        var to = plans[0].Specs[2];
        Assert.Equal(100, fade.EffectiveDurationMs);
        Assert.Equal(250, mid.EffectiveDurationMs);
        Assert.Equal(350, to.EffectiveDurationMs);
    }

    // ---------- 入场：左栏（横向，延迟逐项递减） ----------

    [Fact]
    public void 左栏入场延迟按序号递减()
    {
        // PCL: max(15 - 序号, 7) × 2 → 30, 28, 26, 24 …
        var plans = StaggerPlanner.Build(5, StaggerAxis.Horizontal, StaggerPhase.Enter);
        Assert.Equal([0, 30, 58, 84, 108], plans.Select(p => p.DelayMs));
    }

    [Fact]
    public void 左栏入场延迟有下限()
    {
        // 序号 ≥ 8 时步长落到下限 7 × 2 = 14ms。
        var plans = StaggerPlanner.Build(12, StaggerAxis.Horizontal, StaggerPhase.Enter);
        var steps = new List<int>();
        for (var i = 1; i < plans.Count; i++) steps.Add(plans[i].DelayMs - plans[i - 1].DelayMs);
        Assert.All(steps, step => Assert.True(step >= 14, $"步长 {step} 小于下限 14"));
    }

    [Fact]
    public void 左栏入场初始偏移是负25()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Horizontal, StaggerPhase.Enter);
        Assert.Equal(-25d, plans[0].FromOffset);
    }

    [Fact]
    public void 左栏入场位移量是增量5和20()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Horizontal, StaggerPhase.Enter);
        var translates = plans[0].Specs.Where(s => s.Channel == TweenChannel.TranslateX).ToList();
        Assert.Equal(2, translates.Count);
        Assert.Equal(5d, translates[0].Value);
        Assert.Equal(20d, translates[1].Value);
    }

    [Fact]
    public void 左栏入场时长是100与200与300毫秒()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Horizontal, StaggerPhase.Enter);
        Assert.Equal(100, plans[0].Specs[0].EffectiveDurationMs);
        Assert.Equal(200, plans[0].Specs[1].EffectiveDurationMs);
        Assert.Equal(300, plans[0].Specs[2].EffectiveDurationMs);
    }

    // ---------- 退场：两套不同的延迟算式 ----------

    [Fact]
    public void 右栏退场延迟按恒定15毫秒递增()
    {
        // PCL MyPageRight.cs:648: delay += 15
        var plans = StaggerPlanner.Build(4, StaggerAxis.Vertical, StaggerPhase.Exit);
        Assert.Equal([0, 15, 30, 45], plans.Select(p => p.DelayMs));
    }

    [Fact]
    public void 右栏退场时长是70毫秒()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Exit);
        Assert.All(plans[0].Specs, spec => Assert.Equal(70, spec.EffectiveDurationMs));
    }

    [Fact]
    public void 右栏退场位移量是负6()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Exit);
        var translate = plans[0].Specs.First(s => s.Channel == TweenChannel.TranslateY);
        Assert.Equal(-6d, translate.Value);
    }

    [Fact]
    public void 左栏退场延迟按比例均分在70毫秒内()
    {
        // PCL MyPageLeft.cs:120: round(70 / 总数 × 序号)。
        // 末项 70/4×3 = 52.5 —— PCL 用的是 C# `Math.Round`（**银行家舍入**：
        // 中点取偶数），所以是 52 而不是 53。实现与 PCL 一致，别改成 53。
        var plans = StaggerPlanner.Build(4, StaggerAxis.Horizontal, StaggerPhase.Exit);
        Assert.Equal([0, 18, 35, 52], plans.Select(p => p.DelayMs));
    }

    [Fact]
    public void 左栏退场时长是50毫秒()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Horizontal, StaggerPhase.Exit);
        Assert.All(plans[0].Specs, spec => Assert.Equal(50, spec.EffectiveDurationMs));
    }

    [Fact]
    public void 左栏退场最后一个子项的延迟不超过整体时长()
    {
        var plans = StaggerPlanner.Build(10, StaggerAxis.Horizontal, StaggerPhase.Exit);
        Assert.True(plans[^1].DelayMs <= 70, $"末项延迟 {plans[^1].DelayMs} 超出 70ms");
    }

    [Fact]
    public void 右栏退场延迟与子项总数无关()
    {
        // 固定步进：10 个子项的末项延迟 = 9 × 15 = 135ms，与总数无关。
        var plans = StaggerPlanner.Build(10, StaggerAxis.Vertical, StaggerPhase.Exit);
        Assert.Equal(135, plans[^1].DelayMs);
    }

    // ---------- 可见性 ----------

    [Fact]
    public void 不可见的子项不参与动画()
    {
        var plans = StaggerPlanner.Build(3, StaggerAxis.Vertical, StaggerPhase.Enter, visibleCount: 2);
        Assert.True(plans[0].Visible);
        Assert.True(plans[1].Visible);
        Assert.False(plans[2].Visible);
    }

    [Fact]
    public void 不可见的子项不占用序号()
    {
        // 不可见的项不消耗级联步进 —— 否则可见项之间的间隔会被拉开。
        var plans = StaggerPlanner.Build(3, StaggerAxis.Vertical, StaggerPhase.Enter, visibleCount: 2);
        Assert.Equal(0, plans[0].DelayMs);
        Assert.Equal(25, plans[1].DelayMs);
    }

    [Fact]
    public void 全部不可见时不产生任何动画()
    {
        var plans = StaggerPlanner.Build(3, StaggerAxis.Vertical, StaggerPhase.Enter, visibleCount: 0);
        Assert.All(plans, p => Assert.False(p.Visible));
    }

    [Fact]
    public void 不可见项的规格为空()
    {
        var plans = StaggerPlanner.Build(2, StaggerAxis.Vertical, StaggerPhase.Enter, visibleCount: 1);
        Assert.Empty(plans[1].Specs);
    }

    [Fact]
    public void 中间夹着的不可见项不占用级联序号()
    {
        // PCL 的原版是**任意**跳过不可见项：折叠的子项夹在中间，
        // 前后两个可见项的延迟间隔不应该被拉开。
        var plans = StaggerPlanner.Build(
            3, StaggerAxis.Vertical, StaggerPhase.Enter, null, i => i != 1, null);
        Assert.False(plans[1].Visible);
        Assert.Equal(0, plans[0].DelayMs);
        Assert.Equal(25, plans[2].DelayMs);
    }

    [Fact]
    public void 按索引指定可见性时退场分母是可见项数()
    {
        // 4 项里 3 项可见 → 末项延迟 = round(70 / 3 × 2) = 47（银行家舍入 46.67 → 47）。
        var plans = StaggerPlanner.Build(
            4, StaggerAxis.Horizontal, StaggerPhase.Exit, null, i => i != 1, null);
        Assert.Equal(0, plans[0].DelayMs);
        Assert.Equal(23, plans[2].DelayMs);
        Assert.Equal(47, plans[3].DelayMs);
    }

    // ---------- 边界 ----------

    [Fact]
    public void 负数子项数会抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StaggerPlanner.Build(-1, StaggerAxis.Vertical, StaggerPhase.Enter));
    }

    [Fact]
    public void 零个子项返回空列表()
    {
        Assert.Empty(StaggerPlanner.Build(0, StaggerAxis.Vertical, StaggerPhase.Enter));
    }

    [Fact]
    public void 可见数大于子项数时按子项数处理()
    {
        var plans = StaggerPlanner.Build(2, StaggerAxis.Vertical, StaggerPhase.Enter, visibleCount: 99);
        Assert.All(plans, p => Assert.True(p.Visible));
    }

    [Fact]
    public void 不透明度目标可覆盖()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Enter, opacityTarget: 0.6d);
        Assert.Equal(0.6d, plans[0].OpacityTarget);
        Assert.Equal(0.6d, plans[0].Specs[0].Value);
    }

    [Fact]
    public void 退场的不透明度是负的目标值()
    {
        var plans = StaggerPlanner.Build(1, StaggerAxis.Vertical, StaggerPhase.Exit);
        var opacity = plans[0].Specs.First(s => s.Channel == TweenChannel.Opacity);
        Assert.Equal(-1d, opacity.Value);
    }
}
