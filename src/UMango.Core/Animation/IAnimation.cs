namespace UMango.Core.Animation;

/// <summary>
/// 一个动画。
///
/// 与 PCL-CE 的 `Core/IAnimation.cs` 对应（Apache-2.0）：那边是
/// <c>Name / Status / CurrentFrame / RunAsync / RunFireAndForget / Cancel / GetNextFrame</c>。
/// UMango 保留前四项语义，把"运行"交给 <see cref="AnimationService"/>
/// （由它统一驱动时钟与线程编组），动画本身只负责**按帧求值**。
///
/// 这样切分的好处：动画成为**纯函数式的**对象（给帧号 → 写值），
/// 不持有时钟、不持有 Task、不碰线程 —— 于是可以在 macOS 上用
/// <see cref="Clock.ManualClock"/> 精确驱动并断言每一帧的结果。
/// </summary>
public interface IAnimation
{
    string Name { get; set; }

    AnimationStatus Status { get; }

    /// <summary>总帧数。0 表示瞬时（不产生任何中间帧）。</summary>
    int DurationFrames { get; }

    /// <summary>当前帧号（-1 表示尚未开始）。</summary>
    int CurrentFrame { get; }

    /// <summary>
    /// 求值并把结果写入目标。由驱动器在每一帧调用。
    /// </summary>
    /// <param name="frame">帧号（0 起）</param>
    /// <param name="target">被动画的对象</param>
    void ApplyFrame(int frame, IAnimatable target);

    /// <summary>复位到未开始状态（可重复使用同一个动画对象）。</summary>
    void Reset();

    /// <summary>取消。</summary>
    void Cancel();
}
