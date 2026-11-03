using PCL.Core.UI;
using UMango.Core.Logging;
using UMango.Core.Notifications;
using UMango.Shell.Services;

namespace UMango.Shell.Services;

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
    /// 对话框的**宿主能力**（由界面壳注入）。
    ///
    /// <para>
    /// 为什么要有这个口子：<see cref="MsgBoxWrapper"/> 的委托签名带 <c>ref int result</c>，
    /// 是**同步语义** —— 调用方（可能是后台线程）要立刻拿到按钮值。
    /// WPF 侧要编组到 <c>Dispatcher</c>、Avalonia 侧要编组到 UI 线程，两边机制不同。
    /// 把"怎么显示一个模态对话框并等它关掉"抽成宿主能力，桥接层就与 UI 框架无关了。
    /// </para>
    ///
    /// <para>
    /// 返回值语义：<c>result</c> 是被点击按钮的 <see cref="MsgBoxButtonInfo.Value"/>；
    /// 宿主无法显示时返回 <c>null</c>，桥接层退化为"记日志 + 取第一个按钮的值"。
    /// </para>
    /// </summary>
    public interface IDialogHost
    {
        /// <summary>
        /// 显示模态对话框并等待结果。
        /// </summary>
        /// <param name="message">正文。</param>
        /// <param name="caption">标题。</param>
        /// <param name="buttons">按钮列表（至少一个）。</param>
        /// <param name="theme">主题色。</param>
        /// <param name="block">
        /// 是否阻塞等待。false 时实现可以**立刻**返回默认值（结果未决），
        /// 由界面层自己决定何时关窗。
        /// </param>
        /// <returns>被点击按钮的值；无法显示时返回 null。</returns>
        int? ShowDialog(string message, string caption,
            ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme, bool block);
    }

    /// <summary>
    /// 支持**异步**对话框的宿主（可选能力）。
    ///
    /// <para>
    /// 为什么需要它：<see cref="IDialogHost.ShowDialog"/> 是同步阻塞语义，
    /// WPF 的 <c>Window.ShowDialog()</c> 自带嵌套消息循环，所以在 UI 线程上阻塞没问题；
    /// Avalonia 的 <c>ShowDialog&lt;T&gt;</c> 返回 <c>Task</c> 且**不跑嵌套循环**，
    /// 在 UI 线程上等它就等于死锁。业务方（安装前二次确认）因此改走
    /// <see cref="ShowDialogAsync"/>：宿主是异步实现就 await，是同步实现就照旧直接调用。
    /// </para>
    /// </summary>
    public interface IAsyncDialogHost : IDialogHost
    {
        /// <summary>异步显示模态对话框并等待结果；无法显示时返回 null。</summary>
        Task<int?> ShowDialogAsync(string message, string caption,
            ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme);
    }

    /// <summary>
    /// 显示对话框（优先异步宿主，其次同步宿主）。
    ///
    /// <para>
    /// 没有宿主时返回 <c>null</c>，由调用方决定"不阻断"还是"中止"——
    /// 与 <see cref="OnMsgBox"/> 的降级语义一致：任何 UI 缺失都不该让业务逻辑崩掉。
    /// </para>
    /// </summary>
    public static async Task<int?> ShowDialogAsync(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme)
    {
        var host = DialogHost;
        if (host is null) return null;

        if (host is IAsyncDialogHost asyncHost)
        {
            return await asyncHost.ShowDialogAsync(message, caption, buttons, theme);
        }

        // 同步宿主：由它自己负责线程编组与阻塞（WPF 的 ShowDialog 会跑嵌套消息循环）
        return host.ShowDialog(message, caption, buttons, theme, block: true);
    }

    /// <summary>
    /// 当前生效的对话框宿主。
    ///
    /// 由界面壳在启动时设置（WPF 壳在 <c>App.OnStartup</c>，Avalonia 壳在 <c>App.axaml.cs</c>）。
    /// 未设置时 <see cref="OnMsgBox"/> 走"无界面"降级路径：记日志并返回第一个按钮的值。
    /// </summary>
    public static IDialogHost? DialogHost { get; set; }

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
    ///   1) 把显示动作交给界面宿主（<see cref="DialogHost"/>）——它负责线程编组；
    ///   2) 阻塞等待结果（block=true 时），否则 result 拿不到。
    /// 若没有宿主（单元测试/无界面环境），降级为落日志并返回默认值，
    /// 绝不抛异常 —— 采用 PCL 代码后，任何 UI 缺失都不该让业务逻辑崩掉。
    /// </summary>
    private static void OnMsgBox(string message, string caption,
        ICollection<MsgBoxButtonInfo> buttons, MsgBoxTheme theme, bool block, ref int result)
    {
        var host = DialogHost;
        if (host is null)
        {
            // 无界面环境：记录完整信息（含按钮），让问题可追溯
            var labels = string.Join(" / ", buttons.Select(b => $"{b.Context}={b.Value}"));
            Log.Warn("MsgBox", $"[无界面] {caption}: {message}（按钮：{labels}，主题：{theme}）");
            result = buttons.FirstOrDefault()?.Value ?? 0;
            return;
        }

        var shown = host.ShowDialog(message, caption, buttons, theme, block);
        if (shown is null)
        {
            // 宿主显示不出来：同样降级，绝不抛
            Log.Warn("MsgBox", $"[宿主不可用] {caption}: {message}");
            result = buttons.FirstOrDefault()?.Value ?? 0;
            return;
        }

        result = shown.Value;
    }
}
