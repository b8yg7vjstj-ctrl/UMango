using UMango.App;
using Xunit;

namespace UMango.App.Tests;

/// <summary>
/// 提示浮层的列队策略测试。
///
/// 这两条规则（去重 / 条数上限）是"连续失败时界面会不会糊住"的关键，
/// 抽成纯函数就是为了能在这里锁住 —— 见 ToastPolicy 的类注释。
/// </summary>
public class ToastPolicyTests
{
    [Fact]
    public void 相同文案不入列()
        => Assert.False(ToastPolicy.ShouldEnqueue(["正在安装…"], "正在安装…"));

    [Fact]
    public void 不同文案入列()
        => Assert.True(ToastPolicy.ShouldEnqueue(["正在安装…"], "安装完成"));

    [Fact]
    public void 空文案不入列()
    {
        // PCL 侧偶发传空串（调用方拼串失败），显示空浮条没有意义
        Assert.False(ToastPolicy.ShouldEnqueue([], ""));
        Assert.False(ToastPolicy.ShouldEnqueue([], "   "));
        Assert.False(ToastPolicy.ShouldEnqueue([], null!));
    }

    [Fact]
    public void 去重区分大小写与空白()
    {
        // 用序数比较（不做大小写折叠）——与 PCL 的字符串查重一致
        Assert.True(ToastPolicy.ShouldEnqueue(["ABC"], "abc"));
        Assert.True(ToastPolicy.ShouldEnqueue(["提示 "], "提示"));
    }

    [Fact]
    public void 未达上限时不淘汰()
    {
        Assert.Equal(0, ToastPolicy.EvictionsNeeded(0));
        Assert.Equal(0, ToastPolicy.EvictionsNeeded(3));
    }

    [Fact]
    public void 达到上限时淘汰最旧的()
    {
        // 上限 4：已满 4 条时要腾出 1 个位置
        Assert.Equal(1, ToastPolicy.EvictionsNeeded(4));
        // 异常情形（多于上限）：一次腾够
        Assert.Equal(2, ToastPolicy.EvictionsNeeded(5));
    }

    [Fact]
    public void 上限值与界面预期一致()
        => Assert.Equal(4, ToastPolicy.MaxVisible);
}
