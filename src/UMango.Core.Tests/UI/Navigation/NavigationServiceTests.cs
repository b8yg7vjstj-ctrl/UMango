using UMango.Core.UI.Navigation;
using Xunit;

namespace UMango.Core.Tests.UI.Navigation;

/// <summary>
/// 通用导航栈测试。
///
/// 对应 PCL-CE 的 <c>FormMain.xaml.cs</c>（PageChange / PageBack / PageNameGet），
/// 原版位于受限许可目录，UMango 只借鉴机制重新实现。
/// </summary>
public class NavigationServiceTests
{
    private static NavigationService NewService() => new("home", "主页");

    // ---------- 初始状态 ----------

    [Fact]
    public void 初始页是构造时给定的主页()
    {
        var nav = NewService();

        Assert.Equal("home", nav.Current.Page.Value);
        Assert.Equal("主页", nav.Title);
        Assert.False(nav.CanGoBack);
        Assert.Equal(0, nav.Depth);
    }

    [Fact]
    public void 构造时拒绝null主页()
    {
        Assert.Throws<ArgumentNullException>(() => new NavigationService((NavigationEntry)null!));
    }

    // ---------- 压栈 ----------

    [Fact]
    public void 压栈后当前页变成新页()
    {
        var nav = NewService();
        nav.Push("detail", "详情");

        Assert.Equal("detail", nav.Current.Page.Value);
        Assert.Equal("详情", nav.Title);
        Assert.True(nav.CanGoBack);
        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void 压栈会把原页留在回退栈里()
    {
        var nav = NewService();
        nav.Push("detail");

        Assert.Equal("home", nav.Stack[0].Page.Value);
    }

    [Fact]
    public void 连续压栈形成多层()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b");
        nav.Push("c");

        Assert.Equal(3, nav.Depth);
        Assert.Equal("c", nav.Current.Page.Value);
        Assert.Equal(["home", "a", "b"], nav.Stack.Select(s => s.Page.Value));
    }

    [Fact]
    public void 压栈时记录参数()
    {
        var nav = NewService();
        nav.Push("detail", parameter: 42);

        Assert.Equal(42, nav.Current.Parameter);
    }

    [Fact]
    public void replace压栈不会多压一层()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b", replace: true);

        Assert.Equal(1, nav.Depth);
        Assert.Equal("b", nav.Current.Page.Value);
        Assert.Equal("home", nav.Stack[0].Page.Value);
    }

    [Fact]
    public void 压栈会触发通知()
    {
        var nav = NewService();
        NavigationSnapshot? received = null;
        nav.Navigated += (_, s) => received = s;

        nav.Push("detail", "详情");

        Assert.NotNull(received);
        Assert.Equal("detail", received!.Value.Current.Page.Value);
        Assert.True(received.Value.CanGoBack);
    }

    // ---------- 返回 ----------

    [Fact]
    public void 返回回到上一层()
    {
        var nav = NewService();
        nav.Push("detail");
        nav.Push("more");

        Assert.True(nav.Back());
        Assert.Equal("detail", nav.Current.Page.Value);
        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void 栈空时返回返回false()
    {
        var nav = NewService();
        Assert.False(nav.Back());
    }

    [Fact]
    public void 返回不会弹掉更多层()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b");

        nav.Back();

        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void BackOrRoot在栈空时回主页面()
    {
        var nav = NewService();
        nav.BackOrRoot(new NavigationEntry(new NavigationPageId("home"), "主页"));

        Assert.Equal("home", nav.Current.Page.Value);
    }

    [Fact]
    public void BackOrRoot在有栈时回上一层()
    {
        var nav = NewService();
        nav.Push("a");
        nav.BackOrRoot(new NavigationEntry(new NavigationPageId("home"), "主页"));

        Assert.Equal("home", nav.Current.Page.Value);
        Assert.Equal(0, nav.Depth);
    }

    // ---------- 回主页面 ----------

    [Fact]
    public void 回主页面会清空栈()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b");

        nav.NavigateRoot(new NavigationEntry(new NavigationPageId("home"), "主页"));

        Assert.Equal(0, nav.Depth);
        Assert.False(nav.CanGoBack);
        Assert.Equal("home", nav.Current.Page.Value);
    }

    [Fact]
    public void 回主页面会触发通知()
    {
        var nav = NewService();
        var count = 0;
        nav.Navigated += (_, _) => count++;

        nav.NavigateRoot(new NavigationEntry(new NavigationPageId("home")));

        Assert.Equal(1, count);
    }

    // ---------- 定位到已压入的页 ----------

    [Fact]
    public void 定位到栈中的页会弹掉它上面的层()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b");
        nav.Push("c");

        Assert.True(nav.NavigateTo(new NavigationPageId("a")));
        Assert.Equal("a", nav.Current.Page.Value);
        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void 定位到不存在的页返回false()
    {
        var nav = NewService();
        nav.Push("a");

        Assert.False(nav.NavigateTo(new NavigationPageId("nope")));
    }

    // ---------- 参数与标题 ----------

    [Fact]
    public void 更新参数会触发通知()
    {
        var nav = NewService();
        nav.Push("detail");
        var count = 0;
        nav.Navigated += (_, _) => count++;

        Assert.True(nav.UpdateParameter(7));
        Assert.Equal(7, nav.Current.Parameter);
        Assert.Equal(1, count);
    }

    [Fact]
    public void 更新成相同参数不触发通知()
    {
        var nav = NewService();
        nav.Push("detail", parameter: 7);
        var count = 0;
        nav.Navigated += (_, _) => count++;

        Assert.False(nav.UpdateParameter(7));
        Assert.Equal(0, count);
    }

    [Fact]
    public void 更新标题会触发通知()
    {
        var nav = NewService();
        var count = 0;
        nav.Navigated += (_, _) => count++;

        Assert.True(nav.UpdateTitle("新标题"));
        Assert.Equal("新标题", nav.Title);
        Assert.Equal(1, count);
    }

    [Fact]
    public void 更新成相同标题不触发通知()
    {
        var nav = NewService();
        var count = 0;
        nav.Navigated += (_, _) => count++;

        Assert.False(nav.UpdateTitle("主页"));
        Assert.Equal(0, count);
    }

    // ---------- 暂停通知 ----------

    [Fact]
    public void 暂停期间不通知退出时补发一次()
    {
        var nav = NewService();
        var count = 0;
        nav.Navigated += (_, _) => count++;

        using (nav.SuspendNotifications())
        {
            nav.Push("a");
            nav.Push("b");
            Assert.Equal(0, count);
        }

        Assert.Equal(1, count);
        Assert.Equal("b", nav.Current.Page.Value);
    }

    [Fact]
    public void 暂停后快照仍是最新状态()
    {
        var nav = NewService();
        NavigationSnapshot? received = null;
        nav.Navigated += (_, s) => received = s;

        using (nav.SuspendNotifications())
            nav.Push("a", "A");

        Assert.Equal("a", received!.Value.Current.Page.Value);
        Assert.Equal("A", received!.Value.Title);
    }

    // ---------- 历史 ----------

    [Fact]
    public void 历史记录每次到达的页()
    {
        var nav = NewService();
        nav.Push("a");
        nav.Push("b");
        nav.Back();

        Assert.Equal(["home", "a", "b", "a"], nav.History.Select(h => h.Page.Value));
    }

    // ---------- 快照自洽性 ----------

    [Fact]
    public void 通知里的快照与当前状态一致()
    {
        var nav = NewService();
        NavigationSnapshot? received = null;
        nav.Navigated += (_, s) => received = s;

        nav.Push("a");
        nav.Push("b");

        Assert.Equal(nav.Current, received!.Value.Current);
        Assert.Equal(nav.Depth, received!.Value.Stack.Count);
        Assert.Equal(nav.CanGoBack, received!.Value.CanGoBack);
    }

    [Fact]
    public void 栈属性返回副本外部改不动内部状态()
    {
        var nav = NewService();
        nav.Push("a");

        var copy = nav.Stack.ToList();
        copy.Clear();

        Assert.Equal(1, nav.Depth);
    }
}
