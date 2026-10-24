// SPDX-License-Identifier: Apache-2.0
//
// 可动画控件的收集规则 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：
//   · Plain Craft Launcher 2/Controls/MyPageRight.cs:653-682（`_GetAllAnimControls`）
//   · Plain Craft Launcher 2/Controls/MyPageLeft.cs:139-166（`GetAllAnimControls`）
// （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
// 不复制其代码；本文件为独立重写。
//
// PCL 的两条收集规则**不一样**，这正是 UMango 之前缺的那块：
//
//   右栏（MyPageRight）：
//     if (元素是 MyCard / MyHint / MyExtraTextButton / TextBlock / MyTextButton)
//         收下它，**并且不再往里走**（卡片整体动，卡片内部不动）；
//     else if (元素是 ContentControl) 往它的 Content 里走；
//     else if (元素是 Panel)           往它的子元素走；
//     （其余类型：既不收也不走 —— 直接跳过）
//
//   左栏（MyPageLeft）：
//     if (元素是 MyTextButton / MyListItem) 收下它，**不再往里走**；
//     else if (元素是 ContentControl)        往 Content 走；
//     else if (元素是 Panel)                 往子元素走；
//     else                                    **收下它**（兜底：其它类型也算）
//
// 差别就在最后一条：右栏「跳过未知类型」，左栏「未知类型也要动」。
// 把这个差别写成一个可配置的策略，而不是两套算法。

namespace UMango.Core.Animation;

/// <summary>
/// 遇到「既不是容器、也不是已知可动画控件」的元素时怎么办。
/// </summary>
public enum UnknownNodePolicy
{
    /// <summary>
    /// 跳过（PCL 右栏 <c>MyPageRight</c> 的行为）。
    /// </summary>
    Skip,

    /// <summary>
    /// 收下（PCL 左栏 <c>MyPageLeft</c> 的行为）。
    /// </summary>
    Collect,
}

/// <summary>
/// 可动画控件的收集策略。
///
/// 对应 PCL 两处 <c>GetAllAnimControls</c> 的**类型判定部分**；
/// 树形遍历部分在 <see cref="AnimTreeCollector"/>。
/// </summary>
public sealed record AnimControlPolicy
{
    /// <summary>遇到未知类型时的行为。</summary>
    public UnknownNodePolicy Unknown { get; init; } = UnknownNodePolicy.Skip;

    /// <summary>
    /// 判定某个节点是不是「可动画控件」（即要收下来、且不再往里走的那种）。
    ///
    /// 对应 PCL 的 <c>element is MyCard || element is MyHint || …</c>。
    /// </summary>
    public Func<object, bool> IsAnimatable { get; init; } = _ => false;

    /// <summary>
    /// 判定某个节点是不是「容器」（即应该往里走的那种）。
    ///
    /// 对应 PCL 的 <c>element is ContentControl</c> / <c>element is Panel</c>。
    /// 一个节点可以同时是容器与可动画控件 —— 此时**先按可动画控件处理**（不再往里走），
    /// 这与 PCL 的 <c>if / else if</c> 顺序一致。
    /// </summary>
    public Func<object, bool> IsContainer { get; init; } = _ => false;

    /// <summary>
    /// 取一个容器的子节点。
    ///
    /// 对应 PCL 的 <c>((ContentControl)element).Content</c> 与
    /// <c>((Panel)element).Children</c>。
    /// </summary>
    public Func<object, IEnumerable<object>> GetChildren { get; init; } = _ => [];

    /// <summary>PCL 右栏的策略（未知类型跳过）。</summary>
    public static AnimControlPolicy PclRight { get; } = new()
    {
        Unknown = UnknownNodePolicy.Skip,
    };

    /// <summary>PCL 左栏的策略（未知类型也收下）。</summary>
    public static AnimControlPolicy PclLeft { get; } = new()
    {
        Unknown = UnknownNodePolicy.Collect,
    };
}

/// <summary>
/// 收集可动画控件（树形遍历）。
///
/// 对应 PCL 的 <c>_GetAllAnimControls</c> / <c>GetAllAnimControls</c>（借鉴设计）。
/// 纯泛型 + 委托 → 不依赖任何界面框架，可在 macOS 上直接测试。
/// </summary>
public static class AnimTreeCollector
{
    /// <summary>
    /// 遍历一棵树，收集可动画控件。
    /// </summary>
    /// <param name="roots">根节点（可多个）。</param>
    /// <param name="policy">策略。</param>
    /// <param name="isVisible">
    /// 可见性判定。返回 false 的节点**整棵子树**都跳过
    /// （PCL 的 <c>if (!ignoreInvisibility &amp;&amp; element.Visibility == Collapsed) return;</c>）。
    /// </param>
    /// <param name="ignoreInvisibility">
    /// 为 true 时忽略可见性（PCL 的同名参数）。
    /// </param>
    /// <returns>按遍历顺序（深度优先、先序）收集到的节点。</returns>
    public static IReadOnlyList<object> Collect(
        IEnumerable<object> roots,
        AnimControlPolicy policy,
        Func<object, bool>? isVisible = null,
        bool ignoreInvisibility = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(policy);

        var result = new List<object>();
        foreach (var root in roots)
        {
            if (root is not null) Visit(root, policy, isVisible, ignoreInvisibility, result);
        }
        return result;
    }

    private static void Visit(
        object node,
        AnimControlPolicy policy,
        Func<object, bool>? isVisible,
        bool ignoreInvisibility,
        List<object> result)
    {
        // 不可见的节点整棵子树都跳过。
        if (!ignoreInvisibility && isVisible is not null && !isVisible(node)) return;

        // PCL 的 if / else if 顺序：先判「可动画控件」，命中就收下且**不再往里走**。
        if (policy.IsAnimatable(node))
        {
            result.Add(node);
            return;
        }

        if (policy.IsContainer(node))
        {
            foreach (var child in policy.GetChildren(node))
            {
                if (child is not null) Visit(child, policy, isVisible, ignoreInvisibility, result);
            }
            return;
        }

        // 未知类型：右栏跳过，左栏收下。
        if (policy.Unknown == UnknownNodePolicy.Collect) result.Add(node);
    }
}
