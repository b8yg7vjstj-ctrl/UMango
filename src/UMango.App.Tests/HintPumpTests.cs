using UMango.App.Services;
using UMango.Core.Notifications;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 提示泵的测试。
///
/// <see cref="HintPump"/> 是「Core 队列」与「界面浮条」之间唯一的翻译层，
/// 而这一层里藏着一个**真的会卡死界面**的坑：淘汰时必须回报 Core「这条已经撤了」。
/// 少了回报，Core 的「已显示」列表永远满着 → 每一拍都只返回"挤掉最旧的"，
/// 新提示再也上不了屏。
///
/// 所以这里专门有一条用例盯着"淘汰之后新提示能不能上屏"。
/// </summary>
public sealed class HintPumpTests
{
    [Fact]
    public void 队列为空_不产生任何动作()
    {
        var pump = new HintPump(new HintService());

        Assert.Empty(pump.Pump());
    }

    [Fact]
    public void 有等待的提示_产生显示动作()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        hints.Hint("安装完成");

        var events = pump.Pump();

        var ev = Assert.Single(events);
        Assert.Equal(HintPumpEventKind.Show, ev.Kind);
        Assert.Equal("安装完成", ev.Text);
        Assert.Equal(HintType.Info, ev.Type);
        Assert.True(ev.DurationMs > 0);
    }

    [Fact]
    public void 提示类型_原样传下去()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        hints.Hint("出错了", HintType.Error);

        var ev = Assert.Single(pump.Pump());
        Assert.Equal(HintType.Error, ev.Type);
    }

    [Fact]
    public void 超出上限_先挤掉最旧的再显示新的()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        for (var i = 0; i < HintService.MaxVisible; i++)
        {
            hints.Hint("提示" + i);
            pump.Pump();
        }
        hints.Hint("新的");

        var events = pump.Pump();

        Assert.Equal(2, events.Count);
        Assert.Equal(HintPumpEventKind.Dismiss, events[0].Kind);
        Assert.Equal("提示0", events[0].Text);
        Assert.Equal(HintPumpEventKind.Show, events[1].Kind);
        Assert.Equal("新的", events[1].Text);
    }

    [Fact]
    public void 淘汰之后_新提示确实能上屏()
    {
        // 这条是回归护栏：早期版本在淘汰时**没有**回报 Core，
        // 于是「已显示」列表永远满着 —— 每一拍都只返回"挤掉最旧的"，
        // 新提示再也上不了屏（界面表现为提示彻底卡死）。
        var hints = new HintService();
        var pump = new HintPump(hints);
        for (var i = 0; i < HintService.MaxVisible; i++)
        {
            hints.Hint("提示" + i);
            pump.Pump();
        }
        hints.Hint("新的");

        // 一拍里就完成"挤掉最旧的 → 显示新的"（淘汰回报之后队列立刻有位置了）。
        var events = pump.Pump();

        Assert.Contains(events, e => e.Kind == HintPumpEventKind.Dismiss && e.Text == "提示0");
        Assert.Contains(events, e => e.Kind == HintPumpEventKind.Show && e.Text == "新的");
    }

    [Fact]
    public void 同一拍里_连续处理多条()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        hints.Hint("一");
        hints.Hint("二");
        hints.Hint("三");

        var events = pump.Pump();

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(HintPumpEventKind.Show, e.Kind));
        Assert.Equal(new[] { "一", "二", "三" }, events.Select(e => e.Text));
    }

    [Fact]
    public void 同一拍里_处理的条数有上限()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        for (var i = 0; i < HintPump.MaxActionsPerTick + 3; i++) hints.Hint("提示" + i);

        var events = pump.Pump();

        // 一拍最多产出 8 个动作，剩下的留给下一拍（否则界面会被一次性刷屏动作拖住）。
        Assert.Equal(HintPump.MaxActionsPerTick, events.Count);
        Assert.True(hints.Waiting.Count > 0, "应当还有提示留在队列里等下一拍");
    }
    [Fact]
    public void 已显示中的同文案_不重复显示()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        hints.Hint("重复");
        pump.Pump();

        // 同文案再来 → Core 入列时就查重挡下了
        hints.Hint("重复");

        Assert.Empty(pump.Pump());
    }

    [Fact]
    public void 全部隐藏后_不再产生动作()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);
        hints.Hint("一");
        hints.HideAll();

        Assert.Empty(pump.Pump());
    }

    [Fact]
    public void 绑定的队列就是传入的那个()
    {
        var hints = new HintService();
        var pump = new HintPump(hints);

        Assert.Same(hints, pump.Hints);
    }
}
