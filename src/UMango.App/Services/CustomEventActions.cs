// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// Plain Craft Launcher 2/Modules/Event/CustomEvent.cs。
//
// ⚠️ 那个文件位于 PCL 的**自定义许可层**（《PCL 分发有限许可》），
// 不属于 Apache-2.0 的 PCL.Core，因此 **不逐字复制任何代码**，
// 只借鉴「事件类型 → 动作」的分工方式。
//
// 采用时做的改动：
//   1. 每个动作实现 Core 的 ICustomEventAction 接口，
//      而不像 PCL 那样直接调用静态 ModBase/ModMain —— 这样动作可以被单元测试替换。
//   2. 需要 WPF / 进程 / 剪贴板的能力通过接口注入（IShellCapabilities），
//      App 层提供 WPF 实现，测试里提供假实现。
//   3. PCL 的 MC 专属动作（启动游戏、导入整合包、今日人品、清理垃圾）不迁。
//   4. PCL 的「事件执行前弹确认框」保留为「需要确认的动作」标记。

using System;
using System.Collections.Generic;
using UMango.Core.Customization;

namespace UMango.App.Services;

/// <summary>自定义事件需要用到的系统能力。</summary>
public interface IShellCapabilities
{
    /// <summary>用系统默认程序打开 URL 或文件。</summary>
    void Open(string target);

    /// <summary>把文本放进剪贴板。</summary>
    void CopyToClipboard(string text);

    /// <summary>弹出消息框，返回被点击的按钮索引（0 是取消）。</summary>
    int ShowMessage(string title, string content, string[] buttons);

    /// <summary>弹出提示条。</summary>
    void ShowHint(string message, CustomHintType type);

    /// <summary>触发页面刷新。</summary>
    void RefreshPage();

    /// <summary>下载文件到指定路径。</summary>
    void DownloadFile(string url, string savePath, string? fileName);

    /// <summary>写入一个设置项。</summary>
    void WriteSetting(string key, string value);
}

/// <summary>打开 URL。</summary>
public sealed class OpenUrlEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public OpenUrlEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count == 0)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "缺少链接参数");
        var url = arguments[0].Replace('\\', '/');
        _shell.Open(url);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>打开本地文件。</summary>
public sealed class OpenFileEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public OpenFileEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count == 0)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "缺少文件路径参数");
        if (!confirm($"要打开 {arguments[0]} 吗？"))
            return new CustomEventResult(CustomEventOutcome.CancelledByUser, "用户取消");
        _shell.Open(arguments[0]);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>复制文本到剪贴板。</summary>
public sealed class CopyTextEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public CopyTextEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        _shell.CopyToClipboard(arguments.Count == 0 ? "" : arguments[0]);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>刷新页面。</summary>
public sealed class RefreshPageEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public RefreshPageEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        _shell.RefreshPage();
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>弹出消息框。参数：标题|正文[|按钮文字]。</summary>
public sealed class ShowDialogEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public ShowDialogEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count < 2)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "参数应为 标题|正文[|按钮文字]");
        var title = arguments[0].Replace("\\n", "\n");
        var content = arguments[1].Replace("\\n", "\n");
        var button = arguments.Count > 2 ? arguments[2] : "确定";
        _shell.ShowMessage(title, content, [button]);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>弹出提示条。参数：正文[|类型]。</summary>
public sealed class ShowHintEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public ShowHintEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count == 0)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "缺少提示内容");
        var type = CustomHintType.Info;
        if (arguments.Count > 1)
        {
            // 兼容旧主页的写法：Finish / Critical 是老版本的名字。
            type = arguments[1].Trim().ToLowerInvariant() switch
            {
                "success" or "finish" => CustomHintType.Success,
                "error" or "critical" => CustomHintType.Error,
                _ => CustomHintType.Info,
            };
        }
        _shell.ShowHint(arguments[0].Replace("\\n", "\n"), type);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>下载文件。参数：URL[|保存路径[|文件名]]。</summary>
public sealed class DownloadFileEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public DownloadFileEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count == 0)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "缺少下载地址");
        var url = arguments[0].Replace('\\', '/');
        if (!confirm($"要下载 {url} 吗？"))
            return new CustomEventResult(CustomEventOutcome.CancelledByUser, "用户取消");
        _shell.DownloadFile(url, arguments.Count > 1 ? arguments[1] : "", arguments.Count > 2 ? arguments[2] : null);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}

/// <summary>写入设置。参数：设置名|值。</summary>
public sealed class WriteSettingEventAction : ICustomEventAction
{
    private readonly IShellCapabilities _shell;

    /// <summary>创建动作。</summary>
    public WriteSettingEventAction(IShellCapabilities shell) => _shell = shell;

    /// <inheritdoc />
    public CustomEventResult Execute(IReadOnlyList<string> arguments, Func<string, bool> confirm)
    {
        if (arguments.Count < 2)
            return new CustomEventResult(CustomEventOutcome.InvalidArguments, "参数应为 设置名|值");
        _shell.WriteSetting(arguments[0], arguments[1]);
        return new CustomEventResult(CustomEventOutcome.Success);
    }
}
