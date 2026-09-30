// ============================================================================
// Lang —— PCL.Core/App/Localization/Lang.cs 的**等价桥**（非逐字采用）。
//
// 为什么不是逐字：PCL 的 Lang.cs（289 行）依赖 WPF 的 Application.Current、
// Humanizer NuGet 包与 IoC 生命周期（PCL.Core.App.IoC），并挂载 3,583 行的
// 本地化资源字典。UMango 尚无 i18n 机制（对照清单里「多语言 i18n」为未做），
// 因此这里只提供**与 PCL 同名同签名的契约**，返回 UMango 自己的中文文案。
//
// 这样做的目的：让逐字采用的 PCL 代码（如 Pcl/UI/MsgBoxWrapper.cs）无需改动
// 即可编译运行 —— 与 ModAnimation 的采用策略一致。
// 将来接入真正的 i18n 时，只需替换本文件的实现，调用方一行不用改。
// ============================================================================
using System.Globalization;

namespace PCL.Core.App.Localization;

/// <summary>
/// 本地化文本访问（PCL 契约的等价实现）。
/// 键名与 PCL-CE 保持一致（如 "Common.Action.Confirm"），便于将来对照替换。
/// </summary>
public static class Lang
{
    /// <summary>当前展示区域性（对齐 PCL 契约；UMango 目前不自建区域性切换）。</summary>
    public static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>按键取文本。未登记的键返回键名本身（便于发现遗漏，且不抛异常）。</summary>
    public static string Text(string key) => Texts.TryGetValue(key, out var text) ? text : key;

    /// <summary>带参数的格式化文本，参数按当前区域性格式化。</summary>
    public static string Text(string key, params object?[] args)
    {
        var template = Text(key);
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            // 键值里含未转义大括号等异常格式时，返回原文而不是让界面崩掉
            return template;
        }
    }

    /// <summary>
    /// UMango 侧的文案表。键名沿用 PCL-CE 的键，文案按 UMango 语境撰写。
    /// 目前仅登记被采用代码实际引用的键；接入 i18n 时整体替换本表。
    /// </summary>
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        ["Common.Action.Confirm"] = "确定",
        ["Common.Action.Cancel"] = "取消",
        ["Common.Dialog.Title"] = "提示",
        ["Common.Dialog.Warning"] = "警告",
        ["SystemDialog.Error.Title"] = "错误",
    };
}
