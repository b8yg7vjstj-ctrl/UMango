using System.Windows;
using PCL.Core.UI;
using UMango.App.Views;
using UMango.Shell.Services;

namespace UMango.App.Services;

/// <summary>
/// WPF 侧的对话框宿主：把 <see cref="PclUiBridge"/> 的对话框请求接到 WPF 的
/// <c>Dispatcher</c> 与 <see cref="PclDialog"/> 上。
///
/// <para>
/// 抽成宿主能力而不是写死在桥里，是因为同一套 PCL UI 契约在 Avalonia 壳那边
/// 也要能用（那边没有 <c>Dispatcher</c>，编组方式不同）。桥只认
/// <see cref="PclUiBridge.IDialogHost"/>，两个壳各提供一个实现。
/// </para>
/// </summary>
public sealed class WpfDialogHost : PclUiBridge.IDialogHost
{
    /// <inheritdoc />
    public int? ShowDialog(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme, bool block)
    {
        var app = Application.Current;
        if (app is null) return null;

        var dispatcher = app.Dispatcher;
        if (dispatcher.CheckAccess())
            return PclDialog.ShowDialog(message, caption, theme, buttons, block);

        if (block)
        {
            // 后台线程 + 阻塞语义：同步等到 UI 线程把对话框关掉
            return dispatcher.Invoke(() =>
                PclDialog.ShowDialog(message, caption, theme, buttons, block: true));
        }

        // 非阻塞：把弹窗排到 UI 线程即可，结果按 PCL 语义保持未决（0）
        dispatcher.BeginInvoke(() =>
            PclDialog.ShowDialog(message, caption, theme, buttons, block: false));
        return 0;
    }
}
