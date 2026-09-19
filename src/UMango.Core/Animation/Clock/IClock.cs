namespace UMango.Core.Animation.Clock;

/// <summary>
/// 帧时钟：按帧率驱动动画。
///
/// 设计与 PCL-CE 的 `PCL.Core/UI/Animation/Clock/IClock.cs` 对应
/// （Apache-2.0，可复用）—— 那边也是"时钟发 Tick、动画按帧求值"的**自研帧驱动**模型，
/// **不用 WPF 的 Storyboard/Timeline**。这一点很重要：
/// 正因如此，整个动画引擎是平台中立的，可以在 macOS 上被单测覆盖。
///
/// 与 PCL 的差异（刻意的）：PCL 提供 `IUIClock` 标记接口区分"在 UI 线程上触发的时钟"。
/// UMango 改为在 <see cref="Tick"/> 的文档里写明线程约定，并在 <see cref="AnimationService"/>
/// 里统一编组 —— 少一个标记接口，约束集中在一处更不容易漏。
/// </summary>
public interface IClock
{
    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 帧率上限。设为 <see cref="int.MaxValue"/> 表示尽可能快地触发（渲染驱动时钟用得上）。
    /// </summary>
    int Fps { get; set; }

    void Start();
    void Stop();

    /// <summary>
    /// 每帧触发。参数是从时钟启动算起的**帧序号**（不是时间戳）。
    ///
    /// ⚠️ **线程约定**：实现可能在工作线程上触发（定时器时钟），
    /// 也可能在 UI 线程上触发（渲染驱动时钟）。
    /// 因此订阅者不能假设自己在哪个线程 ——
    /// <see cref="AnimationService"/> 会负责把回调编组回创建动画的那个上下文。
    /// </summary>
    event EventHandler<long>? Tick;
}
