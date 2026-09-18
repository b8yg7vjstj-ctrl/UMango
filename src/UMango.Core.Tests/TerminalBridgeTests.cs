using UMango.Core.Terminal;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>终端集成桥的行为测试。</summary>
public class TerminalBridgeTests
{
    [Fact]
    public void 写入的行按时间正序保留且标记来源()
    {
        var b = TerminalBridge.For("test-order-" + Guid.NewGuid());
        b.Write(false, "第一行");
        b.Write(true, "错误行");

        var lines = b.Lines;
        Assert.Equal(2, lines.Count);
        Assert.Equal("第一行", lines[0].Text);
        Assert.False(lines[0].IsStdErr);
        Assert.True(lines[1].IsStdErr);
    }

    [Fact]
    public void 空白行被忽略()
    {
        var b = TerminalBridge.For("test-blank-" + Guid.NewGuid());
        b.Write(false, "有内容");
        b.Write(false, "");
        b.Write(false, "   ");
        Assert.Single(b.Lines);
    }

    [Fact]
    public void 步骤切换后输出归到对应子项()
    {
        var b = TerminalBridge.For("test-step-" + Guid.NewGuid());
        b.BeginStep("解析依赖");
        b.Write(false, "resolving");
        b.BeginStep("下载");
        b.Write(false, "downloading");

        Assert.Single(b.LinesOfStep("解析依赖"));
        Assert.Single(b.LinesOfStep("下载"));
        Assert.Equal("downloading", b.LinesOfStep("下载")[0].Text);
        Assert.Empty(b.LinesOfStep("不存在的步骤"));
    }

    [Fact]
    public void 行回调能被订阅者实时收到()
    {
        var b = TerminalBridge.For("test-event-" + Guid.NewGuid());
        var received = new List<string>();
        b.LineAppended += l => received.Add(l.Text);

        b.Write(false, "a");
        b.Write(false, "b");

        Assert.Equal(["a", "b"], received);
    }

    [Fact]
    public void 非调试模式下容量受限_调试模式下放宽()
    {
        var b = TerminalBridge.For("test-cap-" + Guid.NewGuid());
        TerminalBridge.DebugMode = false;
        for (var i = 0; i < 500; i++) b.Write(false, $"line-{i}");
        var normal = b.Count;

        b.Clear();
        TerminalBridge.DebugMode = true;
        for (var i = 0; i < 500; i++) b.Write(false, $"line-{i}");
        var debug = b.Count;

        TerminalBridge.DebugMode = false;   // 复位，避免影响其他用例
        Assert.True(normal < 500, $"非调试模式应裁剪历史，实际 {normal}");
        Assert.True(debug > normal, "调试模式应保留更多历史");
    }

    [Fact]
    public void 同名通道是同一个实例_不同名互相隔离()
    {
        var a1 = TerminalBridge.For("shared-channel");
        a1.Clear();
        var a2 = TerminalBridge.For("shared-channel");
        var other = TerminalBridge.For("other-channel-" + Guid.NewGuid());

        a1.Write(false, "x");
        Assert.Same(a1, a2);
        Assert.Equal(1, a2.Count);
        Assert.Equal(0, other.Count);
    }

    [Fact]
    public void 清空会触发事件且行数归零()
    {
        var b = TerminalBridge.For("test-clear-" + Guid.NewGuid());
        var cleared = false;
        b.Cleared += () => cleared = true;
        b.Write(false, "x");
        b.Clear();

        Assert.True(cleared);
        Assert.Empty(b.Lines);
    }
}
