using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.UI;
using UMango.Core.Localization;

namespace UMango.App.Views;

/// <summary>
/// PCL 风格对话框（配合逐字采用的 <see cref="MsgBoxWrapper"/> 使用）。
///
/// 为什么自绘而不是用系统 MessageBox：
///   · PCL 的对话框是**主题化**的（跟随明暗与配色），系统 MessageBox 永远是系统外观；
///   · 需要支持**任意按钮集合与自定义回调**（<see cref="MsgBoxButtonInfo.OnClick"/>），
///     系统 MessageBox 只有固定几种组合；
///   · 按钮文案来自 PCL 契约（<c>MsgBoxWrapper</c> 传入），需要原样呈现。
///
/// 线程模型：对话框**必须在 UI 线程创建**。MsgBoxWrapper.OnShow 是同步委托
/// （带 ref result），因此调用方若是后台线程，桥接层负责编组 —— 见 PclUiBridge。
/// </summary>
public partial class PclDialog : Window
{
    private static string L(string key, params object?[] args)
        => UMango.Core.Localization.LocalizationService.Text(key, args);

    private int _result;

    private PclDialog(string caption, string message, MsgBoxTheme theme,
        ICollection<MsgBoxButtonInfo> buttons, bool block)
    {
        InitializeComponent();

        CaptionText.Text = caption;
        MessageText.Text = message;
        ThemeDot.Fill = ThemeBrush(theme);

        // 无归属窗口时用屏幕居中，否则跟随主窗口（多显示器下位置才正确）
        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        BuildButtons(buttons, block);
    }

    /// <summary>按 MsgBoxTheme 取强调色（复用主题 token，随明暗主题自动变）。</summary>
    private Brush ThemeBrush(MsgBoxTheme theme) => theme switch
    {
        MsgBoxTheme.Error => (Brush)FindResource("BrError"),
        MsgBoxTheme.Warning => (Brush)FindResource("BrWarnAmber"),
        _ => (Brush)FindResource("BrC3"),
    };

    private void BuildButtons(ICollection<MsgBoxButtonInfo> buttons, bool block)
    {
        // 默认按钮 = 第一个（PCL 的 MsgBox 也是这个语义）
        var isFirst = true;
        foreach (var info in buttons)
        {
            var button = new Button
            {
                Content = info.Context,
                MinWidth = 76,
                // WPF StackPanel 无 Spacing，间距由子元素 Margin 提供
                Margin = new Thickness(ButtonHost.Children.Count > 0 ? 8 : 0, 0, 0, 0),
                Style = (Style)FindResource(isFirst ? "PclPrimaryButton" : "PclButton"),
            };
            var captured = info;
            button.Click += (_, _) =>
            {
                _result = captured.Value;
                // 自定义回调先于关闭执行：调用方可能需要在关窗后读状态
                captured.OnClick?.Invoke();
                Close();
            };
            ButtonHost.Children.Add(button);
            isFirst = false;
        }

        // 没有按钮时给一个"确定"，保证对话框一定能关掉（否则界面会卡死）
        if (ButtonHost.Children.Count == 0)
        {
            var ok = new Button
            {
                Content = L("Dialog.Confirm"),
                MinWidth = 76,
                Margin = new Thickness(ButtonHost.Children.Count > 0 ? 8 : 0, 0, 0, 0),
                Style = (Style)FindResource("PclPrimaryButton"),
            };
            ok.Click += (_, _) => Close();
            ButtonHost.Children.Add(ok);
        }
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        // 无边框窗口需要自己实现拖动；双击最大化对对话框无意义，故只支持拖动
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// 显示对话框并返回按钮值（对应 MsgBoxWrapper 的 ref result）。
    /// <paramref name="block"/> 为 true 时阻塞调用线程直到关闭 —— 与 PCL 语义一致。
    /// </summary>
    internal static int ShowDialog(string message, string caption, MsgBoxTheme theme,
        ICollection<MsgBoxButtonInfo> buttons, bool block)
    {
        var dialog = new PclDialog(caption, message, theme, buttons, block);

        if (block)
        {
            dialog.ShowDialog();
            return dialog._result;
        }

        // 非阻塞：结果只能异步获得，调用方拿到的是"未决"值 0（与 PCL 一致）
        dialog.Show();
        return dialog._result;
    }
}
