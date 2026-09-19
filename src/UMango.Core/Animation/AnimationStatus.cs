namespace UMango.Core.Animation;

/// <summary>动画状态。取值与 PCL-CE 的 `AnimationStatus` 一一对应。</summary>
public enum AnimationStatus
{
    /// <summary>未开始</summary>
    NotStarted,
    /// <summary>正在运行</summary>
    Running,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>已取消</summary>
    Canceled
}
