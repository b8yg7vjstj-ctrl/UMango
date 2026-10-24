namespace UMango.Core.Animation;

/// <summary>
/// 动效预设（按主题绑定）。
///
/// 需求原文：「动效之类的在 PCL 主题中完整保留」——
/// 即主题切到 PCL 时，动画参数必须是 PCL-CE 的**原版数值**，不是近似值。
///
/// 数值来源（实测提取，逐项对照；UI 层代码受《PCL 分发有限许可》约束，
/// 我们只提取数值，实现全部自研）：
///   · PCL-CE `Plain Craft Launcher 2/Controls/MyPageRight.cs:579-601`（页面进入）
///   · `MyPageRight.cs:618-623`（页面退出）
///   · `ModAnimation.cs:1293-1306`（AniEaseOutFluent = 1-(1-t)^p，标准幂缓动 Out，
///     数学上与我们的 QuadOut(p=2) 同族；Weak 档位按近似取 SineOut → 注明推断）
/// </summary>
public sealed record PageMotionProfile
{
    /// <summary>入场淡入时长（PCL 实测 100ms）。</summary>
    public int FadeInMs { get; init; }

    /// <summary>入场起始 Y 偏移（PCL 实测 -16，即从上方 16px 落下）。</summary>
    public double EnterFromY { get; init; }

    /// <summary>入场中段目标 Y（PCL 实测 5，配合 BackOut 回弹的"过冲"路径）。</summary>
    public double EnterMidY { get; init; }

    /// <summary>入场终态 Y（PCL 实测 11 —— 注意 PCL 的终态偏移是正的，因为
    /// 它把整页先推开 11px 再落回；我们简化为落回 0，见 CreatePageEnter）。</summary>
    public double EnterToY { get; init; }

    /// <summary>入场中段时长（PCL 实测 250ms）。</summary>
    public int EnterMidMs { get; init; }

    /// <summary>入场末段时长（PCL 实测 350ms，BackOut 回弹）。</summary>
    public int EnterToMs { get; init; }

    /// <summary>
    /// 入场中段缓动。
    ///
    /// PCL 用 <c>AniEaseOutFluent(Middle)</c> = <c>1-(1-t)^3</c>（幂 3）。
    /// 旧版这里用 <see cref="EasingKind.QuadOut"/>（幂 2）近似，**数值并不相同**；
    /// 现在直接用 <see cref="FluentOutEasing"/> 给出原值。
    /// </summary>
    public Easing EnterMidEase { get; init; } = Easing.Of(EasingKind.QuadOut);

    /// <summary>
    /// 入场末段缓动。
    ///
    /// PCL 用 <c>AniEaseOutBack</c>，其指数是 <c>3 - 档位×0.5</c>（随档位变化），
    /// 而不是教科书的固定 1.70158 —— 所以用 <see cref="BackEaseWithPowerEasing"/>。
    /// </summary>
    public Easing EnterToEase { get; init; } = Easing.Of(EasingKind.BackOut);

    /// <summary>
    /// 入场淡入缓动。
    ///
    /// PCL 用 <c>AniEaseOutFluent(Weak)</c> = <c>1-(1-t)^2</c>（幂 2）——
    /// 数值上与 <see cref="EasingKind.QuadOut"/> **恰好相同**，但这里显式用
    /// <see cref="FluentOutEasing"/> 表达原意（弱档幂缓动），不再依赖"巧合相等"。
    /// </summary>
    public Easing FadeInEase { get; init; } = Easing.Of(EasingKind.QuadOut);

    /// <summary>退场时长（PCL 实测 70ms）。</summary>
    public int ExitMs { get; init; }

    /// <summary>退场 Y 偏移（PCL 实测 -6，向上收走）。</summary>
    public double ExitToY { get; init; }

    /// <summary>子项级联延迟（PCL 实测进入 25ms / 退出 15ms）。</summary>
    public int EnterStaggerMs { get; init; }
    public int ExitStaggerMs { get; init; }

    /// <summary>
    /// PCL-CE 原版数值（MyPageRight.cs 实测）。
    /// </summary>
    public static PageMotionProfile Pcl { get; } = new()
    {
        FadeInMs = 100,
        FadeInEase = new FluentOutEasing(EasePower.Weak),   // AniEaseOutFluent(Weak) = 1-(1-t)^2
        EnterFromY = -16,
        EnterMidY = 5,
        EnterToY = 11,                      // PCL 的终态推开量；我们落回 0，此值仅存档对照
        EnterMidMs = 250,
        EnterToMs = 350,
        EnterMidEase = new FluentOutEasing(EasePower.Middle),  // AniEaseOutFluent(Middle) = 1-(1-t)^3
        EnterToEase = new BackEaseWithPowerEasing(EasePower.Middle),  // AniEaseOutBack，指数 3-3×0.5=1.5
        ExitMs = 70,
        ExitToY = -6,
        EnterStaggerMs = 25,
        ExitStaggerMs = 15,
    };

    /// <summary>
    /// Fluent 预设（WinUI 语义）：更短更克制，无过冲。
    /// 用于 Fluent10 主题，让两套主题的动效性格可区分。
    /// </summary>
    public static PageMotionProfile Fluent { get; } = new()
    {
        FadeInMs = 150,
        FadeInEase = new FluentOutEasing(EasePower.Middle),
        EnterFromY = -12,
        EnterMidY = 0,
        EnterToY = 0,
        EnterMidMs = 150,
        EnterToMs = 150,
        EnterMidEase = new FluentOutEasing(EasePower.Middle),
        EnterToEase = new FluentOutEasing(EasePower.Middle),
        ExitMs = 90,
        ExitToY = -8,
        EnterStaggerMs = 0,
        ExitStaggerMs = 0,
    };
}

/// <summary>动效预设选择（与主题绑定：PCL 主题 → PCL 预设，Fluent10 主题 → Fluent 预设）。</summary>
public enum MotionProfileKind
{
    /// <summary>PCL-CE 原版数值（默认，与 PCL 主题配对）。</summary>
    Pcl,

    /// <summary>WinUI/Fluent 语义（与 Fluent10 主题配对）。</summary>
    Fluent,
}
