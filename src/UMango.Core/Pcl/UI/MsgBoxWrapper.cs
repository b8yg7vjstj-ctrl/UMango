// ============================================================================
// 本文件逐字采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）
//   原路径：PCL.Core/UI/MsgBoxWrapper.cs（99 行）
//   许可：Apache-2.0（PCL.Core 层），允许复用；SPDX-License-Identifier: Apache-2.0
// UMango 采用改动（仅 using）：原 using PCL.Core.App.Localization 保留 —— UMango 在
//   同一命名空间提供了等价 Lang.Text 桥（见 Pcl/App/Localization/Lang.cs）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Core.App.Localization;

namespace PCL.Core.UI;

public record MsgBoxButtonInfo(
    string Context,
    int Value = 0,
    Action? OnClick = null
);

public enum MsgBoxTheme
{
    Info,
    Warning,
    Error
}

public delegate void MsgBoxHandler(
    string message,
    string caption,
    ICollection<MsgBoxButtonInfo> buttons,
    MsgBoxTheme theme,
    bool block,
    ref int result
);

public static class MsgBoxWrapper
{
    public static event MsgBoxHandler? OnShow;

    public static int ShowWithCustomButtons(
        string message,
        string? caption,
        MsgBoxTheme theme,
        bool block,
        ICollection<MsgBoxButtonInfo> buttonCollection)
    {
        var result = 0;
        if (buttonCollection.Count == 0)
            buttonCollection = [new MsgBoxButtonInfo(Lang.Text("Common.Action.Confirm"))];
        OnShow?.Invoke(message, _ResolveCaption(caption, theme), buttonCollection, theme, block, ref result);
        return result;
    }

    public static int ShowWithCustomButtons(
        string message,
        string? caption = null,
        MsgBoxTheme theme = MsgBoxTheme.Info,
        bool block = true,
        params MsgBoxButtonInfo[] buttons)
    {
        return ShowWithCustomButtons(message, caption, theme, block, buttonCollection: buttons);
    }

    public static int Show(
        string message,
        string? caption = null,
        MsgBoxTheme theme = MsgBoxTheme.Info,
        bool block = true,
        params string[] buttons)
    {
        var index = 0;
        var list = buttons.Select(button => new MsgBoxButtonInfo(button, ++index)).ToList();
        return ShowWithCustomButtons(message, caption, theme, block, list);
    }

    private static string _ResolveCaption(string? caption, MsgBoxTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(caption)) return caption;

        return theme switch
        {
            MsgBoxTheme.Warning => Lang.Text("Common.Dialog.Warning"),
            MsgBoxTheme.Error => Lang.Text("SystemDialog.Error.Title"),
            _ => Lang.Text("Common.Dialog.Title")
        };
    }
}
