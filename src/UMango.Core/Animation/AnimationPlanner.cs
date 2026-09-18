namespace UMango.Core.Animation;

/// <summary>
/// 动画规划器：把「元素种类 + 全局设置」换算成具体的动画数值。
///
/// **需求落点**（原文：「动画矢量故意镜像 PCL-CE（列表 = 垂直进入 vs PCL 的水平；
/// 对话框 = 右滑 vs PCL 的左滑），并暴露为全局可切换设置」）：
///
///   | 元素 | Mirrored（UMango 默认） | PclCompatible（还原 PCL-CE） |
///   |------|------------------------|----------------------------|
///   | 列表项 | **垂直**进入（+Y → 0） | **水平**进入（+X → 0） |
///   | 对话框 | **向右**滑入（-X → 0） | **向左**滑入（+X → 0） |
///   | 页面内容 | 垂直淡入（+Y → 0） | 水平淡入（+X → 0） |
///   | 轻提示 | 垂直淡入（+Y → 0） | 水平淡入（+X → 0） |
///
/// 两种模式**都实现**，由 <see cref="AnimationSettings.Direction"/> 全局切换 ——
/// 不是"实现一个、把另一个注释掉"。
/// </summary>
public static class AnimationPlanner
{
    /// <summary>默认位移距离（像素）。列表项用较小的值，对话框用较大的值。</summary>
    public const double DefaultDistance = 18;

    /// <summary>对话框默认位移距离</summary>
    public const double DialogDistance = 36;

    /// <summary>默认时长（毫秒）</summary>
    public const int DefaultDurationMs = 220;

    /// <summary>对话框默认时长</summary>
    public const int DialogDurationMs = 180;

    /// <summary>按元素种类与设置算出动画规格。</summary>
    /// <param name="motion">元素种类</param>
    /// <param name="settings">全局设置（可为 null，取默认值）</param>
    /// <param name="distance">覆盖默认位移距离</param>
    public static AnimationSpec Plan(ElementMotion motion, AnimationSettings? settings = null, double? distance = null)
    {
        var s = settings ?? new AnimationSettings();
        var d = distance ?? (motion == ElementMotion.Dialog ? DialogDistance : DefaultDistance);

        if (!s.Enabled)
        {
            // 关掉动画：时长归零、无位移、完全不透明 —— 界面应立即显示终态
            return new AnimationSpec(motion, 0, 0, 0, 0, 1, 1, 0, EasingKind.Linear);
        }

        var baseDuration = motion == ElementMotion.Dialog ? DialogDurationMs : DefaultDurationMs;
        var duration = (int)Math.Round(baseDuration / s.EffectiveSpeedFactor);

        // 缓动：入场类动画用 Out 系（先快后慢），更接近 PCL 的观感
        var easing = motion switch
        {
            ElementMotion.Dialog => EasingKind.CubicOut,
            ElementMotion.ListItem => EasingKind.QuadOut,
            _ => EasingKind.SineOut
        };

        double fromX = 0, fromY = 0;
        var pcl = s.Direction == AnimationDirectionMode.PclCompatible;

        switch (motion)
        {
            case ElementMotion.ListItem:
            case ElementMotion.PageContent:
            case ElementMotion.Tooltip:
                // 镜像模式：垂直进入；PCL 模式：水平进入
                if (pcl) fromX = d; else fromY = d;
                break;

            case ElementMotion.Dialog:
                // 镜像模式：向右滑入（起始在左侧）；PCL 模式：向左滑入（起始在右侧）
                fromX = pcl ? d : -d;
                break;
        }

        return new AnimationSpec(
            Motion: motion,
            FromOffsetX: fromX,
            FromOffsetY: fromY,
            ToOffsetX: 0,
            ToOffsetY: 0,
            FromOpacity: 0,
            ToOpacity: 1,
            DurationMs: duration,
            Easing: easing);
    }

    /// <summary>取某元素在某模式下的起始偏移（供界面直接设置初始 Transform，避免首帧跳变）。</summary>
    public static (double X, double Y) StartOffset(ElementMotion motion, AnimationSettings? settings = null)
    {
        var spec = Plan(motion, settings);
        return (spec.FromOffsetX, spec.FromOffsetY);
    }
}
