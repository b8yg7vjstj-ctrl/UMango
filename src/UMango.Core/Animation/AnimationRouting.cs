namespace UMango.Core.Animation;

/// <summary>
/// 页面切换动画的**路径选择**（纯逻辑，可无界面测试）。
///
/// 两条路径并存，各有来由：
///   · **PCL 式关键帧组合**（<see cref="AnimationPlannerBinding.CreatePageEnter"/>）：
///     淡入 + 两段落体回弹（250/350ms + BackOut），逐字对照 PCL-CE MyPageRight.cs 的调用形状。
///     仅在 **PCL 预设**下使用 —— 这是"动效在 PCL 主题中完整保留"的落点。
///   · **经典单段**（<see cref="AnimationPlanner.Plan"/> + CreateEntrance）：
///     220ms 一段到底、位移/淡入同步。**Fluent10 主题走这条**。
///
/// 为什么 Fluent 不走组合：真机使用反馈确认 Fluent 预设下组合路径更拖沓 ——
/// Fluent 预设没有过冲（中段与终态同为 0），而组合固定生成两段位移，
/// 第二段成了 0 位移空动作却仍占满时长，淡入与位移 150ms 就结束、动画组空转到 300ms。
/// （该空动作缺陷本身已在 CreatePageEnter 里修掉；此处保持"Fluent = 经典单段"的路由，
/// 因为那是用户在 Fluent 主题下认可的观感。）
/// </summary>
public static class AnimationRouting
{
    /// <summary>页面切换是否走 PCL 式关键帧组合。仅 PCL 预设且动画开启时为 true。</summary>
    public static bool UsePclPageCombo(AnimationSettings settings)
        => settings.Enabled && settings.Profile == MotionProfileKind.Pcl;
}
