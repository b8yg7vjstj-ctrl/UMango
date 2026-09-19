namespace UMango.Core.Animation;

/// <summary>
/// 动画基类：处理状态机与帧号边界，子类只实现 <see cref="ApplyFrameCore"/>。
///
/// 把状态机收在这里而不是每个动画各写一遍 —— 这是"降低重复代码率"的一部分，
/// 也保证所有动画对"最后一帧一定是最終值"这件事的处理一致
/// （这一点很容易写错：帧号到不了 DurationFrames 时，动画会停在 99% 的位置）。
/// </summary>
public abstract class AnimationBase : IAnimation
{
    private string? _name;

    /// <summary>动画名。默认取类型名（字段初始化器里不能调 GetType()，故用延迟取值）。</summary>
    public string Name
    {
        get => _name ?? GetType().Name;
        set => _name = value;
    }

    public abstract int DurationFrames { get; }

    public AnimationStatus Status { get; protected set; } = AnimationStatus.NotStarted;

    public int CurrentFrame { get; private set; } = -1;

    public void ApplyFrame(int frame, IAnimatable target)
    {
        // 帧号夹到合法区间：驱动器在边界上可能多给一帧（例如时钟对齐），
        // 这里兜住，避免子类各自处理边界。
        var clamped = Math.Clamp(frame, 0, Math.Max(0, DurationFrames));
        CurrentFrame = clamped;
        Status = AnimationStatus.Running;

        // 进度按 (总帧数) 归一化：第 DurationFrames 帧恰好是 1.0。
        // 用 DurationFrames 而不是 DurationFrames-1 做除数，是为了让"帧号 == 总帧数"
        // 精确对应终态 —— PCL 那边用 totalFrames-1，在短动画上会出现到不了 100% 的偏差。
        var progress = DurationFrames <= 0 ? 1.0 : Math.Clamp((double)clamped / DurationFrames, 0, 1);

        ApplyFrameCore(clamped, progress, target);

        if (clamped >= DurationFrames) Status = AnimationStatus.Completed;
    }

    /// <summary>子类实现：给定帧号与归一化进度，写入目标。</summary>
    protected abstract void ApplyFrameCore(int frame, double progress, IAnimatable target);

    public virtual void Reset()
    {
        CurrentFrame = -1;
        Status = AnimationStatus.NotStarted;
    }

    public virtual void Cancel() => Status = AnimationStatus.Canceled;
}
