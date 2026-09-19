namespace UMango.Core.Animation;

/// <summary>一个"动画 + 目标"的绑定。</summary>
public sealed record AnimationBinding(IAnimation Animation, IAnimatable Target)
{
    public AnimationStatus Status => Animation.Status;
}

/// <summary>
/// 组合动画基类。
///
/// 与 PCL-CE 的 `Core/SequentialAnimationGroup.cs` + `ParallelAnimationGroup.cs` 对应
/// （Apache-2.0）。一处**刻意的差异**：PCL 的组合运行时传入单个 target，
/// 因此"同时位移与淡入"这种需要作用于两个不同属性的场景表达不了
/// （PCL 的 `IAnimatable` 只包一个值）。
/// UMango 改为组内**每个子动画各自带目标** —— 这才是组合动画真正有用的形态。
/// </summary>
public abstract class AnimationGroup : AnimationBase
{
    private readonly List<AnimationBinding> _children = [];

    public IReadOnlyList<AnimationBinding> Children => _children;

    public AnimationGroup Add(IAnimation animation, IAnimatable target)
    {
        _children.Add(new AnimationBinding(animation, target));
        return this;
    }

    public AnimationGroup Add(AnimationBinding binding)
    {
        _children.Add(binding);
        return this;
    }

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        // 组自身的 target 参数被忽略：每个子动画带自己的目标。
        // 之所以仍继承 AnimationBase，是为了复用状态机与帧号边界处理。
        ApplyChildren(frame);
    }

    /// <summary>把组帧号映射到各子动画并求值。</summary>
    protected abstract void ApplyChildren(int groupFrame);
}

/// <summary>顺序动画：子动画一个接一个播放。</summary>
public sealed class SequentialAnimationGroup : AnimationGroup
{
    public override int DurationFrames => Children.Sum(c => Math.Max(0, c.Animation.DurationFrames));

    protected override void ApplyChildren(int groupFrame)
    {
        var offset = 0;
        foreach (var child in Children)
        {
            var duration = Math.Max(0, child.Animation.DurationFrames);

            if (groupFrame < offset)
            {
                // 还没轮到它：保持在起帧（求值第 0 帧），避免它停在未初始化的状态
                child.Animation.ApplyFrame(0, child.Target);
            }
            else if (groupFrame >= offset + duration)
            {
                // 已经演完：固定到终帧（这很关键 —— 否则先演完的子动画会停在中间值）
                child.Animation.ApplyFrame(duration, child.Target);
            }
            else
            {
                child.Animation.ApplyFrame(groupFrame - offset, child.Target);
            }

            offset += duration;
        }
    }
}

/// <summary>并行动画：子动画同时播放，总时长为最长者。</summary>
public sealed class ParallelAnimationGroup : AnimationGroup
{
    public override int DurationFrames => Children.Count == 0
        ? 0
        : Children.Max(c => Math.Max(0, c.Animation.DurationFrames));

    protected override void ApplyChildren(int groupFrame)
    {
        foreach (var child in Children)
        {
            var duration = Math.Max(0, child.Animation.DurationFrames);

            // 短的先演完：固定到终帧，不能让它继续接收越界帧号（否则会被夹到终值但状态误判）
            var childFrame = Math.Min(groupFrame, duration);
            child.Animation.ApplyFrame(childFrame, child.Target);
        }
    }
}
