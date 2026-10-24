namespace UMango.Core.Animation.Clock;

/// <summary>
/// 标记接口：表示"这个时钟在 UI 线程上触发"。
///
/// 与 PCL-CE 的 <c>PCL.Core/UI/Animation/Clock/IUIClock.cs</c> 对应（Apache-2.0）。
/// PCL 那边是一个空标记接口，用来区分渲染驱动时钟与定时器时钟。
///
/// UMango 之前刻意**没有**这个接口（注释里写着"少一个标记接口，约束集中在一处更不容易漏"），
/// 但跨平台复刻到 Avalonia 时又需要它：
/// Avalonia 的渲染驱动时钟**只在 UI 线程**上触发，
/// 而 WPF 的 `CompositionTarget.Rendering` 同样如此 ——
/// 宿主需要能问一句"这个时钟是不是 UI 时钟"，
/// 以便决定是否还要额外编组。
///
/// 因此这里补上该接口，并让 <see cref="StopwatchClock"/> 不实现它
/// （定时器时钟在任意线程触发）、让渲染驱动时钟实现它。
/// </summary>
public interface IUIClock : IClock
{
}
