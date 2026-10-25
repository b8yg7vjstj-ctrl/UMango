// SPDX-License-Identifier: Apache-2.0
//
// 图标登记表测试（对应 PCL-CE `PCL.Core/UI/IconManager.cs`，Apache-2.0；
// 本测试独立编写，不复制 PCL 代码）。

using System;
using System.ComponentModel;
using UMango.Core.UI.Icons;
using Xunit;

namespace UMango.Core.Tests.UI.Icons;

public class IconRegistryTests
{
    private static IconSource Source(string fill = "#123456")
        => new(24, 24, []);

    [Fact]
    public void 登记后可按名取出()
    {
        var reg = new IconRegistry();
        Assert.True(reg.AddIcon("home", Source()));
        Assert.True(reg.TryGet("home", out var model));
        Assert.NotNull(model);
        Assert.Equal("home", model!.Name);
        Assert.True(model.HasContent);
    }

    [Fact]
    public void 名字为空或空白时登记失败()
    {
        var reg = new IconRegistry();
        Assert.False(reg.AddIcon(null, Source()));
        Assert.False(reg.AddIcon("", Source()));
        Assert.False(reg.AddIcon("   ", Source()));
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void 重名不覆盖已有图标()
    {
        var reg = new IconRegistry();
        var first = Source("#111111");
        Assert.True(reg.AddIcon("home", first));
        Assert.False(reg.AddIcon("home", Source("#222222")));
        Assert.True(reg.TryGet("home", out var model));
        Assert.Same(first, model!.Source);
    }

    [Fact]
    public void 取出不存在的名字返回false()
    {
        var reg = new IconRegistry();
        Assert.False(reg.TryGet("nope", out var model));
        Assert.Null(model);
        Assert.False(reg.TryGet(null, out _));
        Assert.False(reg.TryGet("  ", out _));
    }

    [Fact]
    public void 按名选中成功()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        Assert.True(reg.SetSelectedIconByName("home"));
        Assert.Equal("home", reg.SelectedIcon!.Name);
    }

    [Fact]
    public void 按名选中找不到时保持原选中不变()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        reg.SetSelectedIconByName("home");
        Assert.False(reg.SetSelectedIconByName("nope"));
        Assert.NotNull(reg.SelectedIcon);
        Assert.Equal("home", reg.SelectedIcon!.Name);
    }

    [Fact]
    public void 移除图标时若它正被选中则清空选中()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        reg.AddIcon("gear", Source());
        reg.SetSelectedIconByName("home");

        Assert.True(reg.RemoveIconByName("home"));
        // PCL 原版在这里留了个真实缺陷：只从字典删，选中还指着已删的图标。
        Assert.Null(reg.SelectedIcon);
        Assert.False(reg.TryGet("home", out _));
    }

    [Fact]
    public void 移除别的图标不动当前选中()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        reg.AddIcon("gear", Source());
        reg.SetSelectedIconByName("home");

        Assert.True(reg.RemoveIconByName("gear"));
        Assert.Equal("home", reg.SelectedIcon!.Name);
    }

    [Fact]
    public void 移除不存在的名字返回false()
    {
        var reg = new IconRegistry();
        Assert.False(reg.RemoveIconByName("nope"));
        Assert.False(reg.RemoveIconByName(null));
    }

    [Fact]
    public void 选中变化时发出属性变更通知()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        reg.AddIcon("gear", Source());

        string? changed = null;
        reg.PropertyChanged += (s, e) => changed = e.PropertyName;

        reg.SetSelectedIconByName("home");
        Assert.Equal(nameof(IconRegistry.SelectedIcon), changed);

        // 重复选同一个不重复通知
        changed = null;
        reg.SetSelectedIconByName("home");
        Assert.Null(changed);
    }

    [Fact]
    public void 大小写不敏感时忽略大小写取图标()
    {
        var reg = new IconRegistry();
        reg.AddIcon("Home", Source());
        Assert.True(reg.TryGet("home", out _));
        Assert.True(reg.SetSelectedIconByName("HOME"));
    }

    [Fact]
    public void 大小写敏感模式下不忽略大小写()
    {
        var reg = new IconRegistry(caseInsensitive: false);
        reg.AddIcon("Home", Source());
        Assert.False(reg.TryGet("home", out _));
    }

    [Fact]
    public void 清空登记表连同当前选中()
    {
        var reg = new IconRegistry();
        reg.AddIcon("home", Source());
        reg.SetSelectedIconByName("home");

        reg.Clear();

        Assert.Equal(0, reg.Count);
        Assert.Null(reg.SelectedIcon);
    }

    [Fact]
    public void 从SVG源码构造图标成功()
    {
        var source = IconSource.FromSvg("""<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M0 0 L24 24" fill="#123456"/></svg>""");
        Assert.NotNull(source);
        Assert.Equal(24D, source!.Width);
        Assert.NotEmpty(source.Shapes);
    }

    [Fact]
    public void 从非法SVG源码构造返回null()
    {
        Assert.Null(IconSource.FromSvg("<svg><path"));
        Assert.Null(IconSource.FromSvg(""));
        Assert.Null(IconSource.FromSvg(null));
    }

    [Fact]
    public void 登记无内容的图标仍算登记成功但没有内容()
    {
        var reg = new IconRegistry();
        Assert.True(reg.AddIcon("empty", null));
        Assert.True(reg.TryGet("empty", out var model));
        Assert.False(model!.HasContent);
    }
}
