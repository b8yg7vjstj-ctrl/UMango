namespace UMango.Core.Animation.Frames;

/// <summary>
/// 一帧动画。
///
/// 与 PCL-CE 的 <c>PCL.Core/UI/Animation/Core/IAnimationFrame.cs</c> 对应（Apache-2.0）：
/// 那边是 <c>Action GetAction();</c>，配合 <c>FromToAnimationFrame&lt;T&gt;</c> 与
/// <c>ActionAnimationFrame</c> 两个实现。
///
/// **为什么要有"帧"这一层**：UMango 现有的动画是「给帧号 → 直接写目标」，
/// 驱动器调 <see cref="IAnimation.ApplyFrame"/> 就完事。PCL 多一层帧对象，
/// 好处是**可以先把一帧算出来、延后执行**（比如插入到 UI 线程队列的更后面、
/// 或者先收集一帧的所有写入再一起提交）。UMango 保留这一层，
/// 让需要这种批量提交的场景有地方可去 —— 而不必改动所有动画类型。
///
/// 关键设计：帧自己**不执行**，只产出动作；由调用方决定何时执行。
/// </summary>
public interface IAnimationFrame
{
    /// <summary>取出本帧要执行的动作。多次调用返回同一个动作。</summary>
    Action GetAction();
}
