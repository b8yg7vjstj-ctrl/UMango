using PCL.Core.App.Localization;
using PCL.Core.UI;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 逐字采用的 PCL UI 契约（HintWrapper / MsgBoxWrapper）与 Lang 桥的回归锁。
///
/// 这些类型来自 PCL.Core（Apache-2.0），行为必须与原版一致 ——
/// 将来逐字采用更多 PCL 代码时，它们会是被依赖的基础设施。
/// </summary>
public class PclUiContractTests
{
    // ================= HintWrapper =================

    [Fact]
    public void Hint_默认主题为Info_并原样传递消息()
    {
        string? got = null;
        var theme = HintTheme.Info;
        HintHandler handler = (m, t) => { got = m; theme = t; };
        HintWrapper.OnShow += handler;
        try
        {
            HintWrapper.Show("测试提示");
            Assert.Equal("测试提示", got);
            Assert.Equal(HintTheme.Info, theme);
        }
        finally { HintWrapper.OnShow -= handler; }
    }

    [Theory]
    [InlineData(HintTheme.Success)]
    [InlineData(HintTheme.Error)]
    [InlineData(HintTheme.Warning)]
    [InlineData(HintTheme.Info)]
    public void Hint_四种主题都能送达(HintTheme expected)
    {
        HintTheme? got = null;
        HintHandler handler = (_, t) => got = t;
        HintWrapper.OnShow += handler;
        try
        {
            HintWrapper.Show("x", expected);
            Assert.Equal(expected, got);
        }
        finally { HintWrapper.OnShow -= handler; }
    }

    [Fact]
    public void Hint_无订阅者时不抛异常()
    {
        // 采用 PCL 代码后，界面未接线时（测试/无人值守）提示不能炸掉业务逻辑
        var ex = Record.Exception(() => HintWrapper.Show("无人监听"));
        Assert.Null(ex);
    }

    // ================= MsgBoxWrapper =================

    [Fact]
    public void MsgBox_按钮序号从1递增_并回传result()
    {
        int? seenResult = 0;
        MsgBoxHandler handler = (string _, string _, ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme _, bool _, ref int result) =>
        {
            // 原版语义：Show(params string[]) 把按钮按下标赋值为 1,2,3...
            Assert.Equal(new[] { "A", "B", "C" }, buttons.Select(b => b.Context).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, buttons.Select(b => b.Value).ToArray());
            seenResult = 2;
            result = 2;
        };
        MsgBoxWrapper.OnShow += handler;
        try
        {
            var r = MsgBoxWrapper.Show("选择", "标题", MsgBoxTheme.Info, true, "A", "B", "C");
            Assert.Equal(2, r);
            Assert.Equal(2, seenResult);
        }
        finally { MsgBoxWrapper.OnShow -= handler; }
    }

    [Fact]
    public void MsgBox_无按钮时补一个确认按钮()
    {
        List<MsgBoxButtonInfo>? captured = null;
        MsgBoxHandler handler = (string _, string _, ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme _, bool _, ref int _) => captured = buttons.ToList();
        MsgBoxWrapper.OnShow += handler;
        try
        {
            MsgBoxWrapper.ShowWithCustomButtons("空按钮", "标题", MsgBoxTheme.Info, true, new List<MsgBoxButtonInfo>());
            Assert.NotNull(captured);
            Assert.Single(captured!);
            Assert.Equal("确定", captured![0].Context);
        }
        finally { MsgBoxWrapper.OnShow -= handler; }
    }

    [Fact]
    public void MsgBox_空标题按主题取默认标题()
    {
        string? caption = null;
        MsgBoxHandler handler = (string _, string c, ICollection<MsgBoxButtonInfo> _, MsgBoxTheme _, bool _, ref int _) => caption = c;
        MsgBoxWrapper.OnShow += handler;
        try
        {
            MsgBoxWrapper.Show("消息", null, MsgBoxTheme.Warning, true, "好");
            Assert.Equal("警告", caption);
        }
        finally { MsgBoxWrapper.OnShow -= handler; }

        MsgBoxWrapper.OnShow += handler;
        try
        {
            MsgBoxWrapper.Show("消息", null, MsgBoxTheme.Error, true, "好");
            Assert.Equal("错误", caption);
        }
        finally { MsgBoxWrapper.OnShow -= handler; }
    }

    [Fact]
    public void MsgBox_显式标题优先于主题默认值()
    {
        string? caption = null;
        MsgBoxHandler handler = (string _, string c, ICollection<MsgBoxButtonInfo> _, MsgBoxTheme _, bool _, ref int _) => caption = c;
        MsgBoxWrapper.OnShow += handler;
        try
        {
            MsgBoxWrapper.Show("消息", "自定义标题", MsgBoxTheme.Error, true, "好");
            Assert.Equal("自定义标题", caption);
        }
        finally { MsgBoxWrapper.OnShow -= handler; }
    }

    [Fact]
    public void MsgBox_无订阅者时返回0且不抛异常()
    {
        // 与 PCL 一致：没有界面接管时不应崩溃
        var r = MsgBoxWrapper.Show("无人接管", "标题", MsgBoxTheme.Info, true, "好");
        Assert.Equal(0, r);
    }

    // ================= Lang 桥 =================

    [Fact]
    public void Lang_已登记键返回文案()
    {
        Assert.Equal("确定", Lang.Text("Common.Action.Confirm"));
        Assert.Equal("提示", Lang.Text("Common.Dialog.Title"));
    }

    [Fact]
    public void Lang_未登记键返回键名本身()
    {
        // 刻意不抛异常：采用 PCL 代码后，缺失文案应可见但不阻断流程
        Assert.Equal("Some.Missing.Key", Lang.Text("Some.Missing.Key"));
    }

    [Fact]
    public void Lang_格式化参数()
    {
        Assert.Equal("Some.Missing.Key", Lang.Text("Some.Missing.Key", 1, 2));
    }
}
