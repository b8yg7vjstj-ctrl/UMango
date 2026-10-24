// SPDX-License-Identifier: Apache-2.0
//
// WPF 侧的「可动画控件」判定 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：
//   · Plain Craft Launcher 2/Controls/MyPageRight.cs:653-682
//   · Plain Craft Launcher 2/Controls/MyPageLeft.cs:139-166
// （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
// 不复制其代码；本文件为独立重写。
//
// PCL 的类型判定（逐条照抄语义）：
//   右栏：MyCard / MyHint / MyExtraTextButton / TextBlock / MyTextButton
//   左栏：MyTextButton / MyListItem
//
// UMango 没有 PCL 的 MyCard / MyHint / MyListItem 这些自定义控件，
// 所以这里按**角色**判定而不是按 PCL 的具体类型名：
//   · 卡片   → 任何 Border 且带 PclCard 样式（或任何 Border，视作卡片容器）
//   · 标题   → TextBlock
//   · 按钮   → Button 及其子类
//   · 列表项 → ListBoxItem / ListViewItem / ComboBoxItem
//
// 关键取舍：UMango 的页面主体是 `Border Style={PclCard}` 套 `StackPanel`，
// 所以「卡片」这一条覆盖了绝大部分可视子树 —— 与 PCL 效果一致
// （PCL 的 MyCard 同样是「卡片整体动、内部不动」）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UMango.Core.Animation;

namespace UMango.App.Animation;

/// <summary>
/// WPF 版 <see cref="AnimControlPolicy"/>：把 PCL 的类型判定翻译成 WPF 角色判定。
/// </summary>
public static class WpfAnimPolicy
{
    /// <summary>PCL 右栏策略（WPF 版）：卡片 / 文本 / 按钮 / 列表项 视为可动画控件，未知类型跳过。</summary>
    public static AnimControlPolicy Right { get; } = new()
    {
        Unknown = UnknownNodePolicy.Skip,
        IsAnimatable = IsRightAnimatable,
        IsContainer = IsContainer,
        GetChildren = GetChildren,
    };

    /// <summary>PCL 左栏策略（WPF 版）：按钮 / 列表项 / 其它类型 都视为可动画控件。</summary>
    public static AnimControlPolicy Left { get; } = new()
    {
        Unknown = UnknownNodePolicy.Collect,
        IsAnimatable = IsLeftAnimatable,
        IsContainer = IsContainer,
        GetChildren = GetChildren,
    };

    /// <summary>
    /// 右栏的可动画控件判定。
    ///
    /// 对应 PCL 的
    /// <c>element is MyCard || element is MyHint || element is MyExtraTextButton
    /// || element is TextBlock || element is MyTextButton</c>。
    /// </summary>
    private static bool IsRightAnimatable(object node) => node switch
    {
        TextBlock => true,
        Button => true,
        // PCL 的 MyCard 是「卡片」；UMango 的卡片一律是 Border。
        Border => true,
        // PCL 的 MyHint（提示条）→ UMango 里最接近的是带 PclHint 样式的 Border，已被上一条覆盖。
        // PCL 的 MyExtraTextButton（带展开动画的按钮）→ UMango 里是 Button，已被覆盖。
        _ => false,
    };

    /// <summary>
    /// 左栏的可动画控件判定。
    ///
    /// 对应 PCL 的 <c>element is MyTextButton || element is MyListItem</c>。
    /// </summary>
    private static bool IsLeftAnimatable(object node) => node switch
    {
        // ListViewItem 与 ComboBoxItem 都继承自 ListBoxItem —— 只写基类即可，
        // 否则后面的分支会被编译器判为不可达（CS8510）。
        ListBoxItem => true,
        Button => true,
        _ => false,
    };

    /// <summary>
    /// 容器判定。
    ///
    /// 对应 PCL 的 <c>element is ContentControl</c>（走 Content）
    /// 与 <c>element is Panel</c>（走 Children）。
    /// </summary>
    private static bool IsContainer(object node) => node switch
    {
        ContentControl => true,
        Panel => true,
        _ => false,
    };

    /// <summary>
    /// 取子节点。
    ///
    /// 对应 PCL 的 <c>((ContentControl)element).Content</c> 与
    /// <c>((Panel)element).Children</c>。
    ///
    /// 注意：PCL 的顺序是 <c>ContentControl</c> 在前、<c>Panel</c> 在后 ——
    /// 而 <c>ItemsControl</c> 同时是 <c>ContentControl</c>… 不，
    /// <c>ItemsControl</c> 不是 <c>ContentControl</c>，它是 <c>Control</c>。
    /// <c>ContentControl</c> 与 <c>Panel</c> 互斥，所以没有歧义。
    /// </summary>
    private static IEnumerable<object> GetChildren(object node)
    {
        switch (node)
        {
            case ContentControl cc when cc.Content is not null:
                yield return cc.Content;
                break;
            case Panel panel:
                foreach (var child in panel.Children) yield return child;
                break;
        }
    }

    /// <summary>
    /// 可见性判定：<c>Visibility != Collapsed</c>。
    ///
    /// 对应 PCL 的 <c>element.Visibility == Visibility.Collapsed</c> 检查。
    /// </summary>
    public static bool IsVisible(FrameworkElement element) => element.Visibility != Visibility.Collapsed;

    /// <summary>
    /// 收集一棵可视树里的可动画控件。
    /// </summary>
    public static IReadOnlyList<FrameworkElement> Collect(
        FrameworkElement root,
        AnimControlPolicy? policy = null,
        bool ignoreInvisibility = false) =>
        Collect([root], policy, ignoreInvisibility);

    /// <summary>
    /// 收集一棵可视树里的可动画控件（多个根）。
    /// </summary>
    public static IReadOnlyList<FrameworkElement> Collect(
        IEnumerable<FrameworkElement> roots,
        AnimControlPolicy? policy = null,
        bool ignoreInvisibility = false) =>
        AnimTreeCollector.Collect(
            roots.Cast<object>(),
            policy ?? Right,
            node => node is FrameworkElement element && IsVisible(element),
            ignoreInvisibility)
            .OfType<FrameworkElement>()
            .ToList();

    /// <summary>
    /// 找第一���可见的 <see cref="ScrollViewer"/>。
    ///
    /// 对应 PCL 的 <c>GetFirstScrollViewer</c>（MyPageRight.cs:685-706）。
    /// </summary>
    public static ScrollViewer? FindFirstScrollViewer(IEnumerable<FrameworkElement> elements)
    {
        foreach (var element in elements)
        {
            if (element is ScrollViewer viewer)
            {
                if (viewer.ScrollableHeight <= 0 && viewer.ScrollableWidth <= 0) continue;
                return viewer;
            }

            var child = FindFirstScrollViewer(LogicalTreeChildren(element));
            if (child is not null) return child;
        }

        return null;
    }

    private static IEnumerable<FrameworkElement> LogicalTreeChildren(DependencyObject parent)
    {
        // WPF 的 LogicalTreeHelper 只有 GetChild（单数），
        // 没有 GetChildrenCount —— 用 LogicalTreeHelper.GetChildren() 枚举。
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is FrameworkElement element) yield return element;
        }
    }
}
