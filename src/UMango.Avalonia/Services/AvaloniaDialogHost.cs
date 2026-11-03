using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Core.UI;
using UMango.Shell.Services;

namespace UMango.Avalonia.Services;

/// <summary>
/// Avalonia 侧的对话框宿主（对应 WPF 壳的 <c>App/Services/WpfDialogHost.cs</c>）。
///
/// <para>
/// 抽成宿主能力而不是写死在桥里，是因为同一套 PCL UI 契约要在两个壳里都能用。
/// 桥只认 <see cref="PclUiBridge.IDialogHost"/>，两个壳各提供一个实现。
/// </para>
///
/// <para>
/// **与 WPF 实现的关键差异**：Avalonia 的 <c>Window.ShowDialog&lt;T&gt;</c> 返回 Task、
/// 不开嵌套消息循环，所以在 UI 线程上同步等它必然死锁。因此这里实现的是
/// <see cref="PclUiBridge.IAsyncDialogHost"/>（异步能力），业务方走
/// <see cref="PclUiBridge.ShowDialogAsync"/>；同步的 <c>ShowDialog</c> 只保留给
/// PCL 的 <c>MsgBoxWrapper</c> 契约，在 UI 线程上返回 null（桥接层会降级）。
/// </para>
/// </summary>
public sealed class AvaloniaDialogHost : PclUiBridge.IAsyncDialogHost
{
    /// <inheritdoc />
    /// <remarks>
    /// UI 线程上返回 null —— 同步阻塞在 Avalonia 里没有安全的实现方式，
    /// 宁可让桥接层按"无法显示"降级，也不能把界面锁死。
    /// 非 UI 线程则编组到 UI 线程并等待（那里阻塞不会挡住对话框自己）。
    /// </remarks>
    public int? ShowDialog(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme, bool block)
    {
        if (Dispatcher.UIThread.CheckAccess()) return null;

        var completion = new TaskCompletionSource<int?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await ShowDialogAsync(message, caption, buttons, theme));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task.GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<int?> ShowDialogAsync(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme)
    {
        var owner = (Application.Current?.ApplicationLifetime
                        as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return null;

        // 关闭窗口（标题栏 X / Esc）时的兜底结果 = 最后一个按钮的值。
        // 按钮约定与 WPF 侧一致：第一个是主按钮，最后一个是否决按钮 ——
        // 所以"没点任何按钮就关掉"按取消处理，而不是当成确认继续安装。
        var fallback = buttons.LastOrDefault()?.Value ?? 0;
        var result = fallback;

        var dialog = new Window
        {
            Title = caption,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ResolveBrush("BrCard") ?? Brushes.White,
            ShowInTaskbar = false,
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // 第一个按钮用主按钮样式（与 WPF 的 PclPrimaryButton 语义一致）
        var isFirst = true;
        foreach (var info in buttons)
        {
            var button = new Button
            {
                Content = info.Context,
                MinWidth = 80,
                Margin = new Thickness(isFirst ? 0 : 10, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Classes.Add(isFirst ? "pcl-primary" : "pcl-secondary");

            var captured = info;
            button.Click += (_, _) =>
            {
                result = captured.Value;
                // 自定义回调先于关闭执行：调用方可能需要在关窗后读状态
                captured.OnClick?.Invoke();
                dialog.Close();
            };

            buttonRow.Children.Add(button);
            isFirst = false;
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = ResolveBrush("BrG1") ?? Brushes.Black,
                },
                buttonRow,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>按 token 名取画刷（取不到时返回 null，由调用方给兜底色）。</summary>
    private static IBrush? ResolveBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : null;
}
