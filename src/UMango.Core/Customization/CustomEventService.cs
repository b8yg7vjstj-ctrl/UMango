// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// Plain Craft Launcher 2/Modules/Event/（6 文件 836 行）。
//
// ⚠️ 那个目录位于 PCL 的**自定义许可层**（《PCL 分发有限许可》），
// 不属于 Apache-2.0 的 PCL.Core，因此 **不逐字复制任何代码**，
// 只借鉴其「事件类型表 + 动作分发 + 安全黑名单」的架构思路。
//
// 采用时做的设计取舍：
//   1. **不含任何 WPF / XAML 附加属性**。PCL 原版用
//      `CustomEventService`（DependencyProperty.RegisterAttached）把事件绑到
//      任意 FrameworkElement 上，再由 XamlEventSanitizer 清洗远程 XAML。
//      UMango 不加载远程 XAML（见对照表 P2-22 的安全再设计结论），
//      所以不需要附加属性，也就不需要 WPF 依赖 —— 本文件可以放在 Core 里，
//      在 macOS 上被单元测试覆盖。
//   2. **PCL 的 21 个事件类型里，10 个是 Minecraft 专属**
//      （LaunchGame / ImportModpack / InstallModpack / DailyFortune /
//      ClearTrash / 若干 Refresh 语义），对包管理器没有落点 —— 不迁。
//   3. **动作执行全部走接口**（ICustomEventAction），不直接碰进程/剪贴板/窗口，
//      这样「类型 → 动作」的映射表可以被单测逐项验证。
//   4. **安全模型比 PCL 更严**：PCL 只有「高危设置黑名单」+
//      「事件执行前弹确认框」。这里再加上：
//        · 动作注册表默认**拒绝**未注册的类型（白名单而非黑名单）；
//        · 每个动作可声明自己是否需要用户确认；
//        · 内置一个 URL 白名单校验器（只允许 http/https），
//          堵住 file:// / javascript: 之类的协议。
//
// 其余 PCL 专属内容（远程帮助页下载、XAML 渲染、状态变量、实例路径）一并不迁。

using System;
using System.Collections.Generic;
using System.Linq;

namespace UMango.Core.Customization;

/// <summary>自定义事件类型。名称沿用 PCL 的英文名，便于沿用既有主页的写法。</summary>
public enum CustomEventType
{
    None = 0,

    /// <summary>打开网页（参数：URL）。</summary>
    OpenUrl,

    /// <summary>打开文件（参数：路径）。</summary>
    OpenFile,

    /// <summary>复制文本到剪贴板（参数：文本）。</summary>
    CopyText,

    /// <summary>刷新当前页面（参数：可空）。</summary>
    RefreshPage,

    /// <summary>弹出消息框（参数：标题|正文[|按钮文字]）。</summary>
    ShowDialog,

    /// <summary>弹出提示条（参数：正文[|类型]）。</summary>
    ShowHint,

    /// <summary>下载文件（参数：URL[|保存路径[|文件名]]）。</summary>
    DownloadFile,

    /// <summary>写入设置项（参数：设置名|值）。</summary>
    WriteSetting,
}

/// <summary>提示条类型。</summary>
public enum CustomHintType
{
    Info = 0,
    Success = 1,
    Error = 2,
}

/// <summary>自定义事件的执行结果。</summary>
public enum CustomEventOutcome
{
    /// <summary>执行成功。</summary>
    Success = 0,

    /// <summary>该类型没有注册动作（白名单之外）。</summary>
    UnsupportedType,

    /// <summary>参数不足或格式不对。</summary>
    InvalidArguments,

    /// <summary>参数被安全策略拒绝（例如 URL 协议不在白名单）。</summary>
    RejectedByPolicy,

    /// <summary>需要用户确认但用户拒绝了。</summary>
    CancelledByUser,

    /// <summary>动作执行时抛出异常。</summary>
    Failed,
}

/// <summary>自定义事件的执行结果（含消息）。</summary>
/// <param name="Outcome">执行结果。</param>
/// <param name="Message">给用户看的说明（可能为空）。</param>
/// <param name="Detail">给日志看的细节（可能为空）。</param>
public readonly record struct CustomEventResult(
    CustomEventOutcome Outcome,
    string Message = "",
    string Detail = "")
{
    /// <summary>是否成功。</summary>
    public bool Ok => Outcome == CustomEventOutcome.Success;
}

/// <summary>自定义事件的动作执行器。</summary>
public interface ICustomEventAction
{
    /// <summary>执行动作。</summary>
    /// <param name="arguments">按 '|' 分割后的参数。</param>
    /// <param name="confirm">确认回调：需要用户确认时调用它，返回 true 才继续。</param>
    /// <returns>执行结果。</returns>
    CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm);
}

/// <summary>安全策略：决定某个事件是否允许执行。</summary>
public interface ICustomEventPolicy
{
    /// <summary>检查事件。返回 null 表示放行；返回字符串表示拒绝理由。</summary>
    string? Check(CustomEventType type, IReadOnlyList<string> arguments);
}

/// <summary>内置策略：只放行 http/https 链接。</summary>
public sealed class HttpUrlOnlyPolicy : ICustomEventPolicy
{
    /// <summary>共享实例。</summary>
    public static HttpUrlOnlyPolicy Instance { get; } = new();

    /// <inheritdoc />
    public string? Check(CustomEventType type, IReadOnlyList<string> arguments)
    {
        if (type is not (CustomEventType.OpenUrl or CustomEventType.DownloadFile))
            return null;
        if (arguments.Count == 0)
            return "缺少 URL 参数";

        var url = arguments[0].Replace('\\', '/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "只允许 http/https 链接";
        return null;
    }
}

/// <summary>自定义事件服务：把「事件类型 + 参数」分发给注册好的动作。</summary>
public sealed class CustomEventService
{
    private readonly Dictionary<CustomEventType, ICustomEventAction> _actions = new();
    private readonly List<ICustomEventPolicy> _policies = [];

    /// <summary>已注册的类型。</summary>
    public IReadOnlyCollection<CustomEventType> RegisteredTypes => _actions.Keys.ToArray();

    /// <summary>注册一个动作。同类型重复注册时后者覆盖前者。</summary>
    public void Register(CustomEventType type, ICustomEventAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (type == CustomEventType.None)
            throw new ArgumentException("不能为 None 注册动作", nameof(type));
        _actions[type] = action;
    }

    /// <summary>移除一个动作。</summary>
    public bool Unregister(CustomEventType type) => _actions.Remove(type);

    /// <summary>追加一条安全策略。</summary>
    public void AddPolicy(ICustomEventPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies.Add(policy);
    }

    /// <summary>把参数按 '|' 分割。空串返回空数组，保留空片段。</summary>
    public static string[] SplitArguments(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return [];
        return data.Split('|');
    }

    /// <summary>
    /// 执行一个自定义事件。
    /// </summary>
    /// <param name="type">事件类型。</param>
    /// <param name="data">参数串（以 '|' 分隔）。</param>
    /// <param name="confirm">确认回调；不需要确认时传 null。</param>
    public CustomEventResult Raise(CustomEventType type, string? data, Func<string, bool>? confirm = null)
    {
        if (type == CustomEventType.None)
            return new CustomEventResult(CustomEventOutcome.UnsupportedType, "未指定事件类型");

        if (!_actions.TryGetValue(type, out var action))
            return new CustomEventResult(CustomEventOutcome.UnsupportedType, $"不支持的事件类型：{type}");

        var arguments = SplitArguments(data);

        foreach (var policy in _policies)
        {
            var rejection = policy.Check(type, arguments);
            if (rejection is not null)
                return new CustomEventResult(CustomEventOutcome.RejectedByPolicy, rejection);
        }

        try
        {
            return action.Execute(arguments, message => confirm?.Invoke(message) ?? true);
        }
        catch (Exception ex)
        {
            return new CustomEventResult(CustomEventOutcome.Failed, "事件执行失败", ex.Message);
        }
    }

    /// <summary>
    /// 解析事件类型。
    /// 支持英文枚举名、PCL 里的中文别名，以及「刷新主页」这类历史叫法。
    /// </summary>
    public static bool TryParseType(string? value, out CustomEventType type)
    {
        type = CustomEventType.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (Enum.TryParse(text, true, out type) && type != CustomEventType.None)
            return true;

        if (ChineseAliases.TryGetValue(text, out var mapped)
            && Enum.TryParse(mapped, true, out type)
            && type != CustomEventType.None)
            return true;

        type = CustomEventType.None;
        return false;
    }

    /// <summary>中文别名表（沿用 PCL 的写法，兼容既有主页）。</summary>
    private static readonly Dictionary<string, string> ChineseAliases = new(StringComparer.Ordinal)
    {
        ["打开网页"] = nameof(CustomEventType.OpenUrl),
        ["打开文件"] = nameof(CustomEventType.OpenFile),
        ["复制文本"] = nameof(CustomEventType.CopyText),
        ["刷新主页"] = nameof(CustomEventType.RefreshPage),
        ["刷新页面"] = nameof(CustomEventType.RefreshPage),
        ["弹出窗口"] = nameof(CustomEventType.ShowDialog),
        ["弹出提示"] = nameof(CustomEventType.ShowHint),
        ["下载文件"] = nameof(CustomEventType.DownloadFile),
        ["修改设置"] = nameof(CustomEventType.WriteSetting),
        ["写入设置"] = nameof(CustomEventType.WriteSetting),
    };

    /// <summary>PCL 明确标注为不支持的类型（迁到别的产品线了）。</summary>
    public static IReadOnlyCollection<string> UnsupportedTypeNames { get; } =
        new[] { "内存优化", "加入房间", "检查更新" };
}
