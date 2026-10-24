// SPDX-License-Identifier: Apache-2.0
//
// 「功能隐藏」策略的测试。
//
// 对应 PCL-CE 的 `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml.cs:717`
// 的 `HiddenRefresh()`（受限许可目录 → 只借鉴语义、独立重写）。

using UMango.Core.UI.Customization;
using Xunit;

namespace UMango.Core.Tests.UI;

public sealed class HidePolicyTests
{
    [Fact]
    public void 什么都不藏_顶部四个选项卡都在()
    {
        var resolution = HidePolicy.Resolve(new HideEntryPolicy());

        Assert.Equal(4, resolution.TopTabs.Count);
        Assert.Equal(7, resolution.Tools.Count);
        Assert.False(resolution.AllTopTabsHidden);
    }

    [Fact]
    public void 藏掉下载选项卡_它不在结果里()
    {
        var policy = new HideEntryPolicy { Hidden = new HashSet<HideableEntry> { HideableEntry.TabDownload } };

        var resolution = HidePolicy.Resolve(policy);

        Assert.DoesNotContain(HideableEntry.TabDownload, resolution.TopTabs);
        Assert.Contains(HideableEntry.TabProgress, resolution.TopTabs);
    }

    [Fact]
    public void 藏掉工具选项卡_它不在结果里()
    {
        var policy = new HideEntryPolicy { Hidden = new HashSet<HideableEntry> { HideableEntry.TabTools } };

        var resolution = HidePolicy.Resolve(policy);

        Assert.DoesNotContain(HideableEntry.TabTools, resolution.TopTabs);
    }

    [Fact]
    public void 藏掉工具页的日志入口_它不在工具列表里()
    {
        var policy = new HideEntryPolicy { Hidden = new HashSet<HideableEntry> { HideableEntry.ToolLogs } };

        var resolution = HidePolicy.Resolve(policy);

        Assert.DoesNotContain(HideableEntry.ToolLogs, resolution.Tools);
        Assert.Contains(HideableEntry.ToolFeedback, resolution.Tools);
    }

    [Fact]
    public void 强制显示时_藏起来的也照样出现()
    {
        var policy = new HideEntryPolicy
        {
            Hidden = new HashSet<HideableEntry> { HideableEntry.TabDownload, HideableEntry.ToolLogs },
            ForceShow = true,
        };

        var resolution = HidePolicy.Resolve(policy);

        Assert.Contains(HideableEntry.TabDownload, resolution.TopTabs);
        Assert.Contains(HideableEntry.ToolLogs, resolution.Tools);
    }

    [Fact]
    public void 全部藏起来时_标记为全隐藏()
    {
        var policy = new HideEntryPolicy
        {
            Hidden = new HashSet<HideableEntry>
            {
                HideableEntry.TabDownload,
                HideableEntry.TabProgress,
                HideableEntry.TabSettings,
                HideableEntry.TabTools,
            },
        };

        var resolution = HidePolicy.Resolve(policy);

        Assert.True(resolution.AllTopTabsHidden);
        Assert.Empty(resolution.TopTabs);
    }

    [Fact]
    public void 全部藏起来时_仍然留下设置页入口()
    {
        var policy = new HideEntryPolicy
        {
            Hidden = new HashSet<HideableEntry>
            {
                HideableEntry.TabDownload,
                HideableEntry.TabProgress,
                HideableEntry.TabSettings,
                HideableEntry.TabTools,
            },
        };

        var resolution = HidePolicy.Resolve(policy);
        var kept = HidePolicy.EnsureAtLeastOneTab(resolution.TopTabs);

        // 留一个能改回来的入口 —— PCL 原版此时把整条顶部栏藏起来，用户只能改配置文件。
        Assert.Single(kept);
        Assert.Equal(HideableEntry.TabSettings, kept[0]);
    }

    [Fact]
    public void 还有可见选项卡时_不额外补()
    {
        var visible = new[] { HideableEntry.TabProgress };

        var kept = HidePolicy.EnsureAtLeastOneTab(visible);

        Assert.Single(kept);
        Assert.Equal(HideableEntry.TabProgress, kept[0]);
    }

    [Fact]
    public void 强制显示时_不会被判为全隐藏()
    {
        var policy = new HideEntryPolicy
        {
            Hidden = new HashSet<HideableEntry>
            {
                HideableEntry.TabDownload,
                HideableEntry.TabProgress,
                HideableEntry.TabSettings,
                HideableEntry.TabTools,
            },
            ForceShow = true,
        };

        var resolution = HidePolicy.Resolve(policy);

        Assert.False(resolution.AllTopTabsHidden);
        Assert.Equal(4, resolution.TopTabs.Count);
    }
}
