namespace UMango.Core.Animation;

/// <summary>需要播放动画的元素种类。</summary>
public enum ElementMotion
{
    /// <summary>列表项入场（包源分类、搜索结果、任务列表等）</summary>
    ListItem,
    /// <summary>对话框 / 弹出面板（确认框、详情面板）</summary>
    Dialog,
    /// <summary>页面内容切换</summary>
    PageContent,
    /// <summary>轻提示 / 悬浮信息</summary>
    Tooltip
}

/// <summary>
/// 一次动画的完整描述（**与渲染框架无关**，Core 只算数值，WPF 负责播放）。
///
/// 之所以不直接返回 WPF 的 DoubleAnimation：本项目在 macOS 上开发，
/// WPF 类型一被测试工程碰到就没法跑测试（见 DownloaderSession 的注释）；
/// 而且 Core 侧的规格将来换 Avalonia 也能直接复用。
/// </summary>
public sealed record AnimationSpec(
    ElementMotion Motion,
    double FromOffsetX,
    double FromOffsetY,
    double ToOffsetX,
    double ToOffsetY,
    double FromOpacity,
    double ToOpacity,
    int DurationMs,
    EasingKind Easing)
{
    /// <summary>时长归零表示"不播放动画"，界面应立即落到终态</summary>
    public bool IsInstant => DurationMs <= 0;

    /// <summary>水平位移量（绝对值，便于断言"是水平还是垂直"）</summary>
    public double HorizontalDistance => Math.Abs(FromOffsetX - ToOffsetX);

    /// <summary>垂直位移量</summary>
    public double VerticalDistance => Math.Abs(FromOffsetY - ToOffsetY);

    /// <summary>是否以水平位移为主</summary>
    public bool IsHorizontal => HorizontalDistance > VerticalDistance;

    /// <summary>是否以垂直位移为主</summary>
    public bool IsVertical => VerticalDistance > HorizontalDistance;
}
