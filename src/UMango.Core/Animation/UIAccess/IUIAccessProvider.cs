namespace UMango.Core.Animation.UIAccess;

/// <summary>
/// 跨框架的 UI 线程访问接口。
///
/// 与 PCL-CE 的 <c>PCL.Core/UI/Animation/UIAccessProvider/IUIAccessProvider.cs</c> 对应
/// （Apache-2.0）。PCL 为 WPF 提供了 <c>WpfUIAccessProvider</c> 实现；
/// UMango 把它做成**平台中立**的接口，由各宿主提供实现：
/// · WPF 宿主 → <c>Dispatcher.Invoke</c> / <c>Dispatcher.BeginInvoke</c>
/// · Avalonia 宿主 → <c>Dispatcher.UIThread</c> 的对应调用
/// · 测试 → 同步执行（不编组）
///
/// 为什么需要它：动画的每帧求值最终要写界面属性，
/// 而写界面属性**必须在 UI 线程**。时钟可能在工作线程上触发，
/// 于是需要一个"把这段代码搬到 UI 线程去跑"的抽象。
/// 把这个抽象放在 Core（而不是 WPF 侧），
/// 动画引擎才能被 WPF 与 Avalonia **两个外壳共用** —— 这正是跨平台复刻的关键。
/// </summary>
public interface IUIAccessProvider
{
    /// <summary>当前是否已在 UI 线程上。</summary>
    bool CheckAccess();

    /// <summary>在 UI 线程**同步**执行。已在 UI 线程时直接执行。</summary>
    void Invoke(Action action);

    /// <summary>在 UI 线程**异步**执行。已在 UI 线程时直接执行。</summary>
    Task InvokeAsync(Action action);

    /// <summary>在 UI 线程异步执行并返回结果。</summary>
    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>在 UI 线程异步执行 Task。</summary>
    Task InvokeAsync(Func<Task> func);

    /// <summary>在 UI 线程异步执行 Task 并返回结果。</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> func);

    /// <summary>UI 渲染时触发的事件（可选）。</summary>
    event EventHandler? FrameTick;
}
