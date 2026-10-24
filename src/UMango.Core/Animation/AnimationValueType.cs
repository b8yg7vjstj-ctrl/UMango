namespace UMango.Core.Animation;

/// <summary>
/// 动画值的解释方式。
///
/// 与 PCL-CE 的 `Core/AnimationValueType.cs` 对应（Apache-2.0）：那边只有
/// <c>Absolute</c> 与 <c>Relative</c> 两值，语义如下。
/// </summary>
public enum AnimationValueType
{
    /// <summary>
    /// 绝对值：动画值就是目标最终要取到的值。
    ///
    /// 例：从 0 动到 100（不透明度 0 → 1）。
    /// </summary>
    Absolute,

    /// <summary>
    /// 相对值：动画值是**相对于目标当前值的偏移量**，
    /// 动画结束后写回的是「当前值 + 偏移量」。
    ///
    /// 例：目标当前不透明度是 0.5，相对值 0.5 表示动到 1.0。
    /// 相对值是 PCL 那边"多个动画叠加到同一属性"的基础 ——
    /// 每个动画都只关心自己的偏移，不必知道别人把属性改成什么样了。
    /// </summary>
    Relative
}
