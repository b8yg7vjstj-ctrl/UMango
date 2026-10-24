// SPDX-License-Identifier: Apache-2.0
//
// 延迟起播的动画 —— 采用自 PCL-CE（PCL-Community/PCL-CE）。
// 原始文件：Plain Craft Launcher 2/Modules/Base/ModAnimation.cs（**受限许可目录**，
// 按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，不复制其代码。
//
// PCL 的每个 `Aa*` 补间都可以带一个 `delay` 参数 —— 补间在动画开始后的
// 第 delay 毫秒才真正开始推进。UMango 早期版本把这个 delay 丢掉了
// （数值补间一律从第 0 帧就动），这里补上。

namespace UMango.Core.Animation;

/// <summary>
/// 包裹一个动画，让它在 <see cref="DelayFrames"/> 帧之后才开始推进。
///
/// 延迟期内子动画停在第 0 帧 —— 也就是"还没动"的状态。
/// </summary>
public sealed class DelayedAnimation : AnimationBase
{
    private readonly IAnimation _child;
    private readonly int _delayFrames;

    public DelayedAnimation(IAnimation child, int delayFrames)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = child;
        _delayFrames = Math.Max(0, delayFrames);
    }

    /// <summary>被包裹的动画。</summary>
    public IAnimation Child => _child;

    /// <summary>延迟的帧数。</summary>
    public int DelayFrames => _delayFrames;

    public override int DurationFrames => _delayFrames + Math.Max(0, _child.DurationFrames);

    protected override void ApplyFrameCore(int frame, double progress, IAnimatable target)
    {
        if (frame < _delayFrames)
        {
            // 延迟期内：子动画保持在第 0 帧（"还没动"）。
            _child.ApplyFrame(0, target);
            return;
        }

        _child.ApplyFrame(frame - _delayFrames, target);
    }

    public override void Reset()
    {
        base.Reset();
        _child.Reset();
    }

    public override void Cancel()
    {
        base.Cancel();
        _child.Cancel();
    }
}
