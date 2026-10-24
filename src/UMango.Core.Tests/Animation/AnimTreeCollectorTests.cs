// SPDX-License-Identifier: Apache-2.0
//
// 控件树收集规则的测试 —— 对应 PCL-CE 的
//   · Plain Craft Launcher 2/Controls/MyPageRight.cs:653-682
//   · Plain Craft Launcher 2/Controls/MyPageLeft.cs:139-166
// （受限许可目录，只借鉴设计语义）。
//
// 测试用**纯对象图**（不依赖 WPF），所以能在 macOS 上直接跑。

using UMango.Core.Animation;
using Xunit;

namespace UMango.Core.Tests.Animation;

/// <summary>测试用的伪节点。</summary>
public class FakeNode
{
    public FakeNode(string name, params FakeNode[] children)
    {
        Name = name;
        Children = children.ToList();
    }

    public string Name { get; }

    public List<FakeNode> Children { get; }

    /// <summary>模拟 ContentControl：单个 Content。</summary>
    public FakeNode? Content { get; set; }

    public bool Visible { get; set; } = true;
}

/// <summary>模拟 Panel：多个子元素。</summary>
public sealed class FakePanel : FakeNode
{
    public FakePanel(string name, params FakeNode[] children) : base(name, children) { }
}

/// <summary>模拟 ContentControl。</summary>
public sealed class FakeContent : FakeNode
{
    public FakeContent(string name, FakeNode? content = null) : base(name)
    {
        Content = content;
    }
}

/// <summary>模拟「可动画控件」（PCL 的 MyCard / MyTextButton 等）。</summary>
public sealed class FakeAnimatable : FakeNode
{
    public FakeAnimatable(string name, params FakeNode[] children) : base(name, children) { }
}

/// <summary>模拟「既不是容器也不是可动画控件」的未知类型。</summary>
public sealed class FakeUnknown : FakeNode
{
    public FakeUnknown(string name, params FakeNode[] children) : base(name, children) { }
}

public class AnimTreeCollectorTests
{
    private static AnimControlPolicy Policy(UnknownNodePolicy unknown) => new()
    {
        Unknown = unknown,
        IsAnimatable = node => node is FakeAnimatable,
        IsContainer = node => node is FakePanel or FakeContent,
        GetChildren = node => node switch
        {
            FakeContent content => content.Content is null ? [] : [content.Content],
            FakePanel panel => panel.Children,
            _ => [],
        },
    };

    [Fact]
    public void 可动画控件被收下()
    {
        var root = new FakePanel("root", new FakeAnimatable("a"));
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["a"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 命中可动画控件后不再往里走()
    {
        // PCL：卡片整体动，卡片内部不动。
        var inner = new FakeAnimatable("inner");
        var card = new FakeAnimatable("card", inner);
        var root = new FakePanel("root", card);

        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["card"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 容器会继续往里走()
    {
        var leaf = new FakeAnimatable("leaf");
        var root = new FakePanel("root", new FakePanel("mid", leaf));
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["leaf"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void ContentControl走Content()
    {
        var leaf = new FakeAnimatable("leaf");
        var root = new FakeContent("content", leaf);
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["leaf"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 右栏策略跳过未知类型()
    {
        var root = new FakePanel("root", new FakeUnknown("unknown", new FakeAnimatable("hidden")));
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Empty(result);
    }

    [Fact]
    public void 左栏策略收下未知类型()
    {
        var root = new FakePanel("root", new FakeUnknown("unknown"));
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Collect));
        Assert.Equal(["unknown"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 左栏策略收下未知类型后仍会往里走()
    {
        // PCL 左栏：未知类型被收下之后**不再往里走**（因为它不是容器）。
        var root = new FakePanel("root", new FakeUnknown("unknown", new FakeAnimatable("inner")));
        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Collect));
        Assert.Equal(["unknown"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 不可见的节点整棵子树都跳过()
    {
        var leaf = new FakeAnimatable("leaf");
        var mid = new FakePanel("mid", leaf) { Visible = false };
        var root = new FakePanel("root", mid);

        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip), x => ((FakeNode)x).Visible);
        Assert.Empty(result);
    }

    [Fact]
    public void 忽略可见性时不可见节点照样收()
    {
        var leaf = new FakeAnimatable("leaf") { Visible = false };
        var root = new FakePanel("root", leaf);

        var result = AnimTreeCollector.Collect(
            [root], Policy(UnknownNodePolicy.Skip), x => ((FakeNode)x).Visible, ignoreInvisibility: true);
        Assert.Equal(["leaf"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 多个根都会被收集()
    {
        var r1 = new FakePanel("r1", new FakeAnimatable("a"));
        var r2 = new FakePanel("r2", new FakeAnimatable("b"));
        var result = AnimTreeCollector.Collect([r1, r2], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["a", "b"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 遍历顺序是先序深度优先()
    {
        var deep = new FakeAnimatable("deep");
        var mid = new FakePanel("mid", deep);
        var first = new FakeAnimatable("first");
        var root = new FakePanel("root", first, mid);

        var result = AnimTreeCollector.Collect([root], Policy(UnknownNodePolicy.Skip));
        Assert.Equal(["first", "deep"], result.Select(x => ((FakeNode)x).Name));
    }

    [Fact]
    public void 空输入返回空结果()
    {
        var result = AnimTreeCollector.Collect(Array.Empty<FakeNode>(), Policy(UnknownNodePolicy.Skip));
        Assert.Empty(result);
    }

    [Fact]
    public void 容器同时也是可动画控件时优先按可动画控件处理()
    {
        // PCL 的 if / else if 顺序：先判可动画控件。
        var inner = new FakeAnimatable("inner");
        var both = new FakePanel("both", inner);
        var policy = new AnimControlPolicy
        {
            Unknown = UnknownNodePolicy.Skip,
            IsAnimatable = node => node is FakeAnimatable or FakePanel,
            IsContainer = node => node is FakePanel or FakeContent,
            GetChildren = node => node is FakePanel panel ? panel.Children : [],
        };

        var result = AnimTreeCollector.Collect([both], policy);
        Assert.Equal(["both"], result.Select(x => ((FakeNode)x).Name));
    }
}
