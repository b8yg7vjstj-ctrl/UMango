// SPDX-License-Identifier: Apache-2.0
//
// 「功能隐藏」策略：**纯判定层**。
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml.cs:717`
// 的 `HiddenRefresh()`（受限许可目录 → 只借鉴语义，独立重写）。
//
// PCL 原版的行为：
//   · 一批布尔开关决定「哪些页面/入口被藏起来」（顶部下载/设置/工具、设置页各子页、
//     左栏的「选择游戏」等）；
//   · 三个开关是**反向语义**（`FunctionHidden` = 「隐藏隐藏功能这张卡片」，
//     即把「隐藏功能」这个能力本身藏起来），容易读反；
//   · 另有一个「强制显示」的临时总开关（`HiddenForceShow`）能一键恢复全部。
//
// UMango 的差异：
//   · 开关**正名**为「要隐藏什么」，不再有反向语义的「隐藏隐藏功能」；
//   · 判定从「操作 View.Visibility」里抽出来 → 可单测、可复用于别的宿主；
//   · 全部藏起来时，顶部选项卡仍至少留一个（否则用户进不去设置页把设置改回来）——
//     PCL 原版此时直接隐藏整条顶部栏，用户只能改配置文件才能恢复。
//
// 全部判定都在 Core；App 层只负责把结论落到控件的 Visibility 上。

namespace UMango.Core.UI.Customization;

/// <summary>可以被隐藏的入口。</summary>
public enum HideableEntry
{
    /// <summary>顶部「下载」选项卡。</summary>
    TabDownload,

    /// <summary>顶部「进度」选项卡。</summary>
    TabProgress,

    /// <summary>顶部「设置」选项卡。</summary>
    TabSettings,

    /// <summary>顶部「工具」选项卡。</summary>
    TabTools,

    /// <summary>工具页的「下载器」入口。</summary>
    ToolDownloader,

    /// <summary>工具页的「日志」入口。</summary>
    ToolLogs,

    /// <summary>工具页的「反馈」入口。</summary>
    ToolFeedback,

    /// <summary>工具页的「音乐」入口。</summary>
    ToolMusic,

    /// <summary>工具页的「关于」入口。</summary>
    ToolAbout,

    /// <summary>工具页的「合规与许可」入口。</summary>
    ToolLicenses,

    /// <summary>工具页的「诊断」入口。</summary>
    ToolDiagnostics,
}

/// <summary>一组「要隐藏哪些入口」的开关。</summary>
public sealed record HideEntryPolicy
{
    /// <summary>要隐藏的入口集合。</summary>
    public IReadOnlySet<HideableEntry> Hidden { get; init; } =
        new HashSet<HideableEntry>();

    /// <summary>
    /// 临时强制显示：忽略所有隐藏开关，把入口全放出来。
    /// 对应 PCL 的 <c>HiddenForceShow</c>。
    /// </summary>
    public bool ForceShow { get; init; }

    /// <summary>该入口是否应当可见。</summary>
    public bool IsVisible(HideableEntry entry) =>
        ForceShow || !Hidden.Contains(entry);
}

/// <summary>隐藏策略的判定结果。</summary>
/// <param name="TopTabs">顶部选项卡里应当显示的那些。</param>
/// <param name="Tools">工具页左栏里应当显示的那些。</param>
/// <param name="AllTopTabsHidden">
/// 顶部选项卡是否全被藏起来了。App 层据此保证**至少留一个**（否则用户进不去设置页）。
/// </param>
public readonly record struct HideResolution(
    IReadOnlyList<HideableEntry> TopTabs,
    IReadOnlyList<HideableEntry> Tools,
    bool AllTopTabsHidden);

/// <summary>隐藏策略的判定器（纯函数）。</summary>
public static class HidePolicy
{
    /// <summary>顶部选项卡候选（顺序即显示顺序）。</summary>
    public static readonly HideableEntry[] TopTabCandidates =
    [
        HideableEntry.TabDownload,
        HideableEntry.TabProgress,
        HideableEntry.TabSettings,
        HideableEntry.TabTools,
    ];

    /// <summary>工具页入口候选（顺序即显示顺序）。</summary>
    public static readonly HideableEntry[] ToolCandidates =
    [
        HideableEntry.ToolDownloader,
        HideableEntry.ToolLogs,
        HideableEntry.ToolDiagnostics,
        HideableEntry.ToolLicenses,
        HideableEntry.ToolFeedback,
        HideableEntry.ToolMusic,
        HideableEntry.ToolAbout,
    ];

    /// <summary>
    /// 判定哪些入口应当显示。
    /// </summary>
    /// <param name="policy">隐藏开关集合。</param>
    public static HideResolution Resolve(HideEntryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var top = TopTabCandidates.Where(policy.IsVisible).ToArray();
        var tools = ToolCandidates.Where(policy.IsVisible).ToArray();

        return new HideResolution(top, tools, top.Length == 0);
    }

    /// <summary>
    /// 顶部选项卡全被藏起来时，**强行留下一个**。
    ///
    /// 留哪一个：优先「设置」（用户唯一能改回来的入口），
    /// 设置不存在时退回第一个（下载/进度/工具里的任意一个）。
    ///
    /// 理由：PCL 原版此时把整条顶部栏隐藏掉，用户只能去改配置文件才能恢复 ——
    /// 那不是"隐藏功能"，那是把用户锁在门外。UMango 保留这个能力但不至于此。
    /// </summary>
    public static IReadOnlyList<HideableEntry> EnsureAtLeastOneTab(
        IReadOnlyList<HideableEntry> visible,
        HideableEntry fallback = HideableEntry.TabSettings)
    {
        ArgumentNullException.ThrowIfNull(visible);

        if (visible.Count > 0) return visible;

        return new[] { fallback };
    }
}
