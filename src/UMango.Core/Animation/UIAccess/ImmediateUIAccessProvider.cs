namespace UMango.Core.Animation.UIAccess;

/// <summary>
/// 同步执行的 <see cref="IUIAccessProvider"/>：所有调用都在当前线程立即跑。
///
/// 用途：
/// · 单元测试（不需要真的编组到 UI 线程）；
/// · 没有界面线程的宿主（无头模式、命令行工具）；
/// · 已经保证在 UI 线程上调用时，充当"零开销"实现。
///
/// ⚠️ **误用的后果**：在 WPF/Avalonia 的工作线程上用它写界面属性会抛
/// "跨线程访问"异常。它**不做检查**（检查本身有开销，且界面框架自己也会检查），
/// 用的人要自己清楚线程上下文。
/// </summary>
public sealed class ImmediateUIAccessProvider : IUIAccessProvider
{
    /// <summary>共享实例（无状态，可安全共享）。</summary>
    public static ImmediateUIAccessProvider Instance { get; } = new();

    public bool CheckAccess() => true;

    public void Invoke(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

    public Task InvokeAsync(Func<Task> func) => func();

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

    public event EventHandler? FrameTick
    {
        add { }
        remove { }
    }
}
