namespace UMango.Core.Animation;

/// <summary>
/// 动画方向模式。
///
/// 需求原文：「动画矢量故意镜像 PCL-CE（列表 = 垂直进入 vs PCL 的水平；
/// 对话框 = 右滑 vs PCL 的左滑），并暴露为全局可切换设置。」
///
/// 也就是说这两个模式**都必须存在**，用户可在设置里切换 ——
/// 这不是"实现一个、注释掉另一个"，所以两种矢量都在代码里有明确取值。
/// </summary>
public enum AnimationDirectionMode
{
    /// <summary>
    /// UMango 自有（默认）：列表项**垂直**进入、对话框**向右**滑入。
    /// 「向右滑入」= 起始在左侧偏移、终态归位，视觉上整体向右移动。
    /// </summary>
    Mirrored,

    /// <summary>
    /// 与 PCL-CE 一致：列表项**水平**进入、对话框**向左**滑入。
    /// 「向左滑入」= 起始在右侧偏移、终态归位，视觉上整体向左移动。
    /// 想还原 PCL-CE 观感的用户切到这个模式。
    /// </summary>
    PclCompatible
}

/// <summary>
/// 全局动画设置。
/// 默认值对着 PCL-CE 的语义：
///   · 动画默认开启；
///   · 帧率上限沿用 PCL 的 UiAniFPS 默认值 59（见 PCL.Core/App/Config.cs）；
///   · 速度倍率 1.0 表示不加速。
/// </summary>
public sealed class AnimationSettings
{
    /// <summary>是否启用动画。关掉后所有动画时长归零（不播放，也没有残影）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>方向模式（见 <see cref="AnimationDirectionMode"/>）</summary>
    public AnimationDirectionMode Direction { get; set; } = AnimationDirectionMode.Mirrored;

    /// <summary>
    /// 速度倍率。&gt;1 更快、&lt;1 更慢。
    /// 与 PCL 的 `AnimationSpeed` 调试项用途一致（放慢观察动画细节）。
    /// </summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>帧率上限，默认 59 —— 取自 PCL-CE 的 Config.System.AnimationFpsLimit 默认值</summary>
    public int FpsLimit { get; set; } = 59;

    /// <summary>
    /// 动效预设（默认 PCL 原版数值）。
    /// 需求：「动效之类的在 PCL 主题中完整保留」——预设由 ThemeManager 随主题同步
    /// （PCL 主题 → Pcl 预设；Fluent10 主题 → Fluent 预设），用户改设置可覆盖。
    /// </summary>
    public MotionProfileKind Profile { get; set; } = MotionProfileKind.Pcl;

    /// <summary>把速度倍率夹到合理范围，避免 0 或负数导致除零/负时长。</summary>
    public double EffectiveSpeedFactor => Math.Clamp(SpeedFactor, 0.1, 10.0);

    public AnimationSettings Clone() => new()
    {
        Enabled = Enabled,
        Direction = Direction,
        SpeedFactor = SpeedFactor,
        FpsLimit = FpsLimit,
        Profile = Profile
    };
}
