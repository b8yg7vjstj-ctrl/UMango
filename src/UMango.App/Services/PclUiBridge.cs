using System.Windows;
using PCL.Core.UI;
using UMango.App.Views;
using UMango.Core.Logging;
using UMango.Core.Notifications;

namespace UMango.App.Services;

/// <summary>
/// PCL UI 契约 → UMango 界面的桥接。
///
/// PCL-CE 把"提示"和"对话框"抽象成两个纯事件契约（<see cref="HintWrapper"/>、
/// <see cref="MsgBoxWrapper"/>，均属 Apache-2.0 层，已逐字采用进 UMango.Core）。
/// 界面层只需订阅事件即可；PCL 侧调用方（将来逐字采用的代码）不必知道 UI 细节。
///
/// 本类就是那个订阅方：
///   · Hint → <see cref="NotificationService"/>（主题映射 1:1）
///   · MsgBox → <see cref="PclDialog"/>（主题化对话框，支持自定义按钮与回调）
///
/// 为什么单独成类而不是塞进 AppServices：这是**可替换的适配层** ——
/// Avalonia 壳将来提供自己的实现即可，Core 与调用方都不用改。
/// </summary>
public static class PclUiBridge
{
    private static bool _wired;

    /// <summary>接线（幂等）。应在 UI 线程上调用 —— 事件处理器本身会做线程编组。</summary>
    public static void Wire(INotificationService notifications)
    {
        if (_wired) return;
        _wired = true;

        HintWrapper.OnShow += (message, theme) => OnHint(notifications, message, theme);
        MsgBoxWrapper.OnShow += OnMsgBox;

        Log.Info("PCL", "PCL UI 契约已接线（HintWrapper / MsgBoxWrapper）");
    }

    /// <summary>
    /// Hint 主题 → 通知类型的映射（纯函数，便于测试）。
    /// 两个枚举一一对应，因此这里不做任何"智能"转换 —— 保持 1:1 才不会有信息损失。
    /// </summary>
    public static NotificationType MapTheme(HintTheme theme) => theme switch
    {
        HintTheme.Success => NotificationType.Success,
        HintTheme.Error => NotificationType.Error,
        HintTheme.Warning => NotificationType.Warning,
        _ => NotificationType.Info,
    };

    /// <summary>
    /// 提示：映射到通知服务，并同步落日志。
    /// PCL 的 HintService 也会记录日志（`Hint(..., log: true)`），这里保持同样行为 ——
    /// 提示一闪而过，日志是事后唯一的凭据。
    /// </summary>
    private static void OnHint(INotificationService notifications, string message, HintTheme theme)
    {
        // 日志级别与图标/色条同源：都按 theme 决定，避免"日志说成功、界面显红"这类不一致
        switch (theme)
        {
            case HintTheme.Error:
                Log.Error("Hint", message);
                break;
            case HintTheme.Warning:
                Log.Warn("Hint", message);
                break;
            default:
                Log.Info("Hint", message);
                break;
        }

        notifications.Show(new Notification { Message = message, Type = MapTheme(theme) });
    }

    /// <summary>
    /// 对话框：MsgBoxWrapper 的委托签名带 <c>ref int result</c>，是**同步语义** ——
    /// 调用方（可能是后台线程）要立刻拿到按钮值，因此这里必须：
    ///   1) 编组到 UI 线程创建窗口；
    ///   2) 阻塞等待结果（block=true 时），否则 result 拿不到。
    /// 若没有 Application（单元测试/无界面环境），降级为落日志并返回默认值，
    /// 绝不抛异常 —— 采用 PCL 代码后，任何 UI 缺失都不该让业务逻辑崩掉。
    /// </summary>
    private static void OnMsgBox(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme, bool block, ref int result)
    {
        var app = Application.Current;

        if (app is null)
        {
            // 无界面环境：记录完整信息（含按钮），让问题可追溯
            var labels = string.Join(" / ", buttons.Select(b => $"{b.Context}={b.Value}"));
            Log.Warn("MsgBox", $"[无界面] {caption}: {message}（按钮：{labels}，主题：{theme}）");
            result = buttons.FirstOrDefault()?.Value ?? 0;
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            result = PclDialog.ShowDialog(message, caption, theme, buttons, block);
            return;
        }

        if (block)
        {
            // 后台线程 + 阻塞语义：同步等到 UI 线程把对话框关掉
            result = app.Dispatcher.Invoke(() =>
                PclDialog.ShowDialog(message, caption, theme, buttons, block: true));
        }
        else
        {
            // 非阻塞：把弹窗排到 UI 线程即可，结果按 PCL 语义保持未决（0）
            app.Dispatcher.BeginInvoke(() =>
                PclDialog.ShowDialog(message, caption, theme, buttons, block: false));
        }
    }
}
