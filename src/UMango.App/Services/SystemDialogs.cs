// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/SystemDialogs.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.App.Services；
//   2. 原版直接 new 具体的 Microsoft.Win32 对话框，无法测试；
//      这里把「参数计算」与「显示对话框」分开：
//      参数计算是纯函数，可以被单元测试；
//      显示对话框走 IFileDialogHost 接口，测试里可以塞假实现。
//   3. 原版用 PCL 自己的 Lang.Text 取默认标题；
//      这里走本仓库的本地化桥（LocalizationService.Text），
//      键名沿用 PCL 的 SystemDialog.* 以便对照。
//   4. 原版的 SelectFolder 强制返回带尾部分隔符的路径；
//      这个「以 \ 结尾」的约定在 Windows 上还有用，
//      但对跨平台 shell 是陷阱（macOS/Linux 路径不以 / 结尾也没问题）。
//      改为返回规范化后的路径，并另提供 EnsureTrailingSeparator 供确实需要的调用方使用。
//   5. 原版在 initialDirectory 不存在时传 null 给对话框；
//      这里统一先校验目录存在，避免对话框跳到奇怪的位置。

using System;
using System.Collections.Generic;
using System.IO;
using UMango.Core.Localization;

namespace UMango.App.Services;

/// <summary>
/// 文件/文件夹选择对话框的抽象。
///
/// 存在的理由：直接 new <c>Microsoft.Win32.OpenFileDialog</c> 的代码无法被单元测试
/// （它在非 Windows 上根本没有实现，构造就抛 PlatformNotSupportedException）。
/// 把「显示」抽成接口之后，参数计算与调用顺序都能被验证。
/// </summary>
public interface IFileDialogHost
{
    /// <summary>显示「打开文件」对话框。返回选中的路径数组；取消返回空数组。</summary>
    string[] ShowOpenFiles(FileDialogRequest request);

    /// <summary>显示「保存文件」对话框。返回完整路径；取消返回空字符串。</summary>
    string ShowSaveFile(FileDialogRequest request);

    /// <summary>显示「选择文件夹」对话框。返回完整路径；取消返回空字符串。</summary>
    string ShowOpenFolder(FileDialogRequest request);
}

/// <summary>一次文件对话框请求的参数。</summary>
public sealed class FileDialogRequest
{
    /// <summary>对话框标题。</summary>
    public required string Title { get; init; }

    /// <summary>过滤器（Windows 风格，如 "所有文件|*.*"）。</summary>
    public string Filter { get; init; } = "";

    /// <summary>默认文件名（仅保存对话框有意义）。</summary>
    public string DefaultFileName { get; init; } = "";

    /// <summary>初始目录；为空或不存在时由系统决定。</summary>
    public string? InitialDirectory { get; init; }

    /// <summary>是否允许多选。</summary>
    public bool AllowMultiple { get; init; }
}

/// <summary>
/// 文件与文件夹选择对话框。
///
/// 语义沿用 PCL-CE 的 <c>SystemDialogs</c>：取消一律返回空字符串/空数组，
/// 而不是 null —— 调用方不必区分「取消」与「出错」。
/// </summary>
public static class SystemDialogs
{
    /// <summary>默认的「所有文件」过滤器。</summary>
    public const string AllFilesFilter = "所有文件|*.*";

    /// <summary>计算打开/保存对话框的标题：调用方给了就用调用方的，否则用本地化默认值。</summary>
    public static string ResolveTitle(string? title, string fallbackKey)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title;
        return LocalizationService.Text(fallbackKey);
    }

    /// <summary>计算过滤器：调用方给了就用调用方的，否则用本地化默认值。</summary>
    public static string ResolveFilter(string? fileFilter, string fallbackKey)
    {
        if (!string.IsNullOrWhiteSpace(fileFilter))
            return fileFilter;
        return LocalizationService.Text(fallbackKey);
    }

    /// <summary>
    /// 校验初始目录：不存在就返回 null（由系统决定），
    /// 存在则返回规范化后的完整路径。
    /// </summary>
    public static string? ResolveInitialDirectory(string? initialDirectory)
    {
        if (string.IsNullOrWhiteSpace(initialDirectory))
            return null;
        try
        {
            if (!Directory.Exists(initialDirectory))
                return null;
            return Path.GetFullPath(initialDirectory);
        }
        catch (Exception)
        {
            // 非法路径（含有非法字符等）不应该让对话框打不开。
            return null;
        }
    }

    /// <summary>把选中的路径转成完整路径；转换失败则返回原值。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>确保路径以目录分隔符结尾（PCL 原版对文件夹选择结果的处理）。</summary>
    public static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    /// <summary>打开单文件选择对话框。取消返回空字符串。</summary>
    public static string SelectFile(IFileDialogHost host, string? fileFilter = null, string? title = null, string? initialDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = host.ShowOpenFiles(new FileDialogRequest
        {
            Title = ResolveTitle(title, "SystemDialog.File.SelectTitle"),
            Filter = ResolveFilter(fileFilter, "SystemDialog.File.AllFilesFilter"),
            InitialDirectory = ResolveInitialDirectory(initialDirectory),
            AllowMultiple = false,
        });
        return result.Length == 0 ? "" : NormalizePath(result[0]);
    }

    /// <summary>打开多文件选择对话框。取消返回空数组。</summary>
    public static string[] SelectFiles(IFileDialogHost host, string? fileFilter = null, string? title = null, string? initialDirectory = null, bool allowMultiSelect = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = host.ShowOpenFiles(new FileDialogRequest
        {
            Title = ResolveTitle(title, "SystemDialog.File.SelectTitle"),
            Filter = ResolveFilter(fileFilter, "SystemDialog.File.AllFilesFilter"),
            InitialDirectory = ResolveInitialDirectory(initialDirectory),
            AllowMultiple = allowMultiSelect,
        });
        if (result.Length == 0)
            return [];
        var list = new List<string>(result.Length);
        foreach (var path in result)
            list.Add(NormalizePath(path));
        return list.ToArray();
    }

    /// <summary>打开保存文件对话框。取消返回空字符串。</summary>
    public static string SelectSaveFile(IFileDialogHost host, string? title, string fileName, string? fileFilter = null, string? initialDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = host.ShowSaveFile(new FileDialogRequest
        {
            Title = ResolveTitle(title, "SystemDialog.File.SelectTitle"),
            Filter = ResolveFilter(fileFilter, "SystemDialog.File.AllFilesFilter"),
            DefaultFileName = fileName ?? "",
            InitialDirectory = ResolveInitialDirectory(initialDirectory),
        });
        return result.Length == 0 ? "" : NormalizePath(result);
    }

    /// <summary>打开文件夹选择对话框。取消返回空字符串。</summary>
    public static string SelectFolder(IFileDialogHost host, string? title = null, string? initialDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = host.ShowOpenFolder(new FileDialogRequest
        {
            Title = ResolveTitle(title, "SystemDialog.Folder.SelectTitle"),
            InitialDirectory = ResolveInitialDirectory(initialDirectory),
        });
        return result.Length == 0 ? "" : NormalizePath(result);
    }
}
