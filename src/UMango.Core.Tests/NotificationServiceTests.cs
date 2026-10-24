using UMango.Core.Notifications;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 通知服务与提示服务队列的测试。
///
/// <see cref="NotificationService"/> 是 UMango 自有的通知中枢；
/// <see cref="HintService"/> 的语义对齐 PCL-CE 的 <c>HintService</c>
/// （受限许可目录，只借鉴语义、重新实现），
/// 但修掉了原版的几个真实缺陷：空白提示、查重口径不一致、停留时长可能被算成 0。
/// </summary>
public sealed class NotificationServiceTests
{
    // ─────────────────────────── NotificationService ───────────────────────────

    [Fact]
    public void 通知_显示_进入队列并触发事件()
    {
        var service = new NotificationService();
        Notification? shown = null;
        service.OnShow += n => shown = n;

        service.ShowSuccess("成功");

        Assert.Single(service.Queue);
        Assert.NotNull(shown);
        Assert.Equal("成功", shown!.Message);
        Assert.Equal(NotificationType.Success, shown.Type);
    }

    [Fact]
    public void 通知_四种类型_类型正确()
    {
        var service = new NotificationService();
        service.ShowSuccess("a");
        service.ShowError("b");
        service.ShowWarning("c");
        service.ShowInfo("d");

        Assert.Equal(
            new[] { NotificationType.Success, NotificationType.Error, NotificationType.Warning, NotificationType.Info },
            service.Queue.Select(n => n.Type));
    }

    [Fact]
    public void 通知_清空_队列清空并触发事件()
    {
        var service = new NotificationService();
        var cleared = false;
        service.OnClear += () => cleared = true;
        service.ShowInfo("x");

        service.ClearAll();

        Assert.Empty(service.Queue);
        Assert.True(cleared);
    }

    [Fact]
    public void 通知_默认时长三秒()
    {
        var service = new NotificationService();
        service.ShowInfo("x");
        Assert.Equal(3000, service.Queue[0].DurationMs);
    }

    // ────────────────────────────── 文案归一 ──────────────────────────────

    [Theory]
    [InlineData("你好", "你好")]
    [InlineData("  你好  ", "你好")]
    [InlineData("你好\n世界", "你好 世界")]
    [InlineData("你好\r\n世界", "你好 世界")]
    [InlineData("你好\r世界", "你好 世界")]
    [InlineData("你  好", "你 好")]
    [InlineData("\t你好\t", "你好")]
    public void 归一化_按规则处理(string input, string expected)
    {
        Assert.Equal(expected, HintService.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void 归一化_空白输入_返回空串(string? input)
    {
        Assert.Equal(string.Empty, HintService.Normalize(input));
    }

    // ────────────────────────────── 入列 ──────────────────────────────

    [Fact]
    public void 入列_普通提示_入列成功()
    {
        var service = new HintService();
        Assert.True(service.Hint("安装完成"));
        Assert.Single(service.Waiting);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    public void 入列_空白文案_被丢弃(string? text)
    {
        // 回归：原版不裁剪首尾空白，空白文案会显示成一个只有图标的空浮条
        var service = new HintService();
        Assert.False(service.Hint(text));
        Assert.Empty(service.Waiting);
    }

    [Fact]
    public void 入列_同文案同类型_只入列一次()
    {
        var service = new HintService();
        Assert.True(service.Hint("重复"));
        Assert.False(service.Hint("重复"));
        Assert.Single(service.Waiting);
    }

    [Fact]
    public void 入列_同文案不同类型_都入列()
    {
        var service = new HintService();
        Assert.True(service.Hint("一样的话", HintType.Info));
        Assert.True(service.Hint("一样的话", HintType.Error));
        Assert.Equal(2, service.Waiting.Count);
    }

    [Fact]
    public void 入列_已显示中的同文案_不入列()
    {
        // 回归：原版只在**等待队列**里查重，已显示的那条不参与 →
        //       重复提示仍会占一个队列槽位。
        var service = new HintService();
        service.Hint("已经在屏幕上");
        service.Tick();                       // 这条显示出来了

        Assert.False(service.Hint("已经在屏幕上"));
        Assert.Empty(service.Waiting);
    }

    [Fact]
    public void 入列_归一化后相同_视为重复()
    {
        var service = new HintService();
        Assert.True(service.Hint("安装完成"));
        Assert.False(service.Hint("  安装完成\n"));
        Assert.Single(service.Waiting);
    }

    [Fact]
    public void 入列并取回_返回带时长的消息()
    {
        var service = new HintService();
        var msg = service.HintAndGet("有内容");
        Assert.NotNull(msg);
        Assert.Equal("有内容", msg!.Text);
        Assert.True(msg.DurationMs > 0);
    }

    [Fact]
    public void 入列并取回_重复_返回空()
    {
        var service = new HintService();
        Assert.NotNull(service.HintAndGet("x"));
        Assert.Null(service.HintAndGet("x"));
    }

    // ────────────────────────────── 停留时长 ──────────────────────────────

    [Fact]
    public void 停留时长_正常倍率_在合理区间()
    {
        var d = HintService.ComputeDurationMs("短");
        Assert.InRange(d, HintService.MinDurationMs, HintService.MaxDurationMs);
    }

    [Fact]
    public void 停留时长_动画加速为零_不会变成零()
    {
        // 回归：原版乘 ModAnimation.aniSpeed，该值为 0 时提示条刚显示就消失
        var d = HintService.ComputeDurationMs("重要提示", animationSpeed: 0d);
        Assert.Equal(HintService.MinDurationMs, d);
    }

    [Fact]
    public void 停留时长_文案越长_停留越久()
    {
        var shortText = HintService.ComputeDurationMs("短");
        var longText = HintService.ComputeDurationMs("这是一条很长的提示文案内容需要更久的停留时间");
        Assert.True(longText >= shortText);
    }

    [Fact]
    public void 停留时长_超长文案_按长度上限截断()
    {
        // 与 PCL 同构的公式：800 + clamp(len, 5, 23) * 180
        // → 文案再长也只按 23 字算，最长 800 + 23 * 180 = 4940ms
        var expected = 800 + 23 * 180;
        Assert.Equal(expected, HintService.ComputeDurationMs(new string('x', 5000)));
        // 倍率放大到硬上限
        Assert.Equal(HintService.MaxDurationMs,
            HintService.ComputeDurationMs(new string('x', 5000), animationSpeed: 3d));
    }

    [Fact]
    public void 停留时长_不会超过硬上限()
    {
        // 硬上限是兜底，任何输入都不得越界
        foreach (var speed in new[] { 0.1d, 1d, 5d, 100d })
        {
            Assert.InRange(HintService.ComputeDurationMs(new string('x', 5000), speed),
                HintService.MinDurationMs, HintService.MaxDurationMs);
        }
    }

    [Fact]
    public void 停留时长_非法倍率_按一倍处理()
    {
        var d = HintService.ComputeDurationMs("x", animationSpeed: double.NaN);
        Assert.InRange(d, HintService.MinDurationMs, HintService.MaxDurationMs);
    }

    // ────────────────────────────── Tick ──────────────────────────────

    [Fact]
    public void Tick_队列为空_什么都不做()
    {
        var service = new HintService();
        Assert.Equal(HintActionKind.None, service.Tick().Kind);
    }

    [Fact]
    public void Tick_有等待_取出并显示()
    {
        var service = new HintService();
        service.Hint("第一条");

        var action = service.Tick();

        Assert.Equal(HintActionKind.Show, action.Kind);
        Assert.Equal("第一条", action.Message!.Text);
        Assert.Empty(service.Waiting);
    }

    [Fact]
    public void Tick_按先进先出()
    {
        var service = new HintService();
        service.Hint("一");
        service.Hint("二");

        Assert.Equal("一", service.Tick().Message!.Text);
        Assert.Equal("二", service.Tick().Message!.Text);
    }

    [Fact]
    public void Tick_达到上限_挤掉最旧的一条()
    {
        var service = new HintService();
        for (var i = 0; i < HintService.MaxVisible; i++)
        {
            service.Hint("提示" + i);
            service.Tick();
        }
        Assert.Equal(HintService.MaxVisible, service.Displayed.Count);

        service.Hint("再来一条");
        var action = service.Tick();

        Assert.Equal(HintActionKind.DismissOldest, action.Kind);
        Assert.Equal("提示0", action.Text);
    }

    [Fact]
    public void Tick_挤掉最旧后_下一拍才显示新的()
    {
        var service = new HintService();
        for (var i = 0; i < HintService.MaxVisible; i++)
        {
            service.Hint("提示" + i);
            service.Tick();
        }
        service.Hint("新的");
        Assert.Equal(HintActionKind.DismissOldest, service.Tick().Kind);

        // 界面层照做（把最旧的关掉并回报）
        service.NotifyDismissed("提示0");
        Assert.Equal(HintActionKind.Show, service.Tick().Kind);
    }

    [Fact]
    public void Tick_已显示中的同文案_不新叠一条()
    {
        var service = new HintService();
        service.Hint("重复");
        service.Tick();

        // 同文案的再次到来在入列时就被查重拦下 → 队列空 → 无动作
        Assert.False(service.Hint("重复"));
        Assert.Equal(HintActionKind.None, service.Tick().Kind);
    }

    // ────────────────────────────── 关闭与清空 ──────────────────────────────

    [Fact]
    public void 提示关闭_从已显示列表移除()
    {
        var service = new HintService();
        service.Hint("会消失");
        service.Tick();
        Assert.Single(service.Displayed);

        service.NotifyDismissed("会消失");
        Assert.Empty(service.Displayed);
    }

    [Fact]
    public void 提示关闭_不存在的文案_不抛()
    {
        var service = new HintService();
        service.NotifyDismissed("从来没出现过");
        Assert.Empty(service.Displayed);
    }

    [Fact]
    public void 全部隐藏_清空等待与已显示()
    {
        var service = new HintService();
        service.Hint("一");
        service.Tick();
        service.Hint("二");

        service.HideAll();

        Assert.Empty(service.Waiting);
        Assert.Empty(service.Displayed);
        Assert.Equal(HintActionKind.None, service.Tick().Kind);
    }

    [Fact]
    public void 全部隐藏_之后可以重新入列()
    {
        var service = new HintService();
        service.Hint("一");
        service.Tick();
        service.HideAll();

        Assert.True(service.Hint("一"));
    }

    // ────────────────────────────── 线程安全 ──────────────────────────────

    [Fact]
    public void 并发入列_不崩且不重复()
    {
        var service = new HintService();
        Parallel.For(0, 50, i => service.Hint("并发" + (i % 10)));
        Assert.Equal(10, service.Waiting.Count);
    }

    // ────────────────────── 通知服务 ↔ 提示队列（统一策略源） ──────────────────────

    [Fact]
    public void 通知_会同时进入提示队列()
    {
        var service = new NotificationService();

        service.ShowSuccess("安装完成");

        Assert.Single(service.Hints.Waiting);
        Assert.Equal("安装完成", service.Hints.Waiting[0].Text);
        Assert.Equal(HintType.Success, service.Hints.Waiting[0].Type);
    }

    [Fact]
    public void 通知_与提示队列共享同一个实例()
    {
        var hints = new HintService();
        var service = new NotificationService(hints);

        Assert.Same(hints, service.Hints);
    }

    [Fact]
    public void 通知_重复文案_提示队列只留一条()
    {
        var service = new NotificationService();

        service.ShowInfo("重复");
        service.ShowInfo("重复");

        Assert.Single(service.Hints.Waiting);
    }

    [Fact]
    public void 通知_清空_同时清掉提示队列()
    {
        var service = new NotificationService();
        service.ShowInfo("a");
        service.Hints.Tick();

        service.ClearAll();

        Assert.Empty(service.Hints.Waiting);
        Assert.Empty(service.Hints.Displayed);
    }

    [Fact]
    public void 全部隐藏_会触发清空事件()
    {
        var service = new HintService();
        var cleared = 0;
        service.Cleared += () => cleared++;

        service.HideAll();

        Assert.Equal(1, cleared);
    }

    [Fact]
    public void 全部隐藏_事件只触发一次()
    {
        var service = new HintService();
        var cleared = 0;
        service.Cleared += () => cleared++;

        service.HideAll();
        service.HideAll();

        Assert.Equal(2, cleared);
    }
}
