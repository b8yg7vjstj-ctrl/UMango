// SPDX-License-Identifier: Apache-2.0
//
// 本文件的思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/SystemDialogs.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.App.Services；
//   2. 原版把对话框直接 new 在方法里，无法测试；
//      这里把「参数计算」与「显示对话框」分开：
//      参数计算是纯函数，可以被单元测试；
//      显示对话框走 IFileDialogHost 接口，测试里可以塞假实现。
//   3. 原版直接调用 ShowDialog()，在无 owner 窗口时会挂在窗口后面；
//      这里改用 ShowDialog(owner) 重载，把对话框挂到 owner 窗口上。
//   4. 原版 SelectFolder 强制返回带尾部分隔符的路径；
//      这个「以 \ 结尾」的约定在 Windows 上还有用，
//      但对跨平台 shell 是陷阱（macOS/Linux 路径不以 / 结尾也没问题）。
//      改为返回规范化后的路径，并另提供 EnsureTrailingSeparator 供确实需要的调用方使用。
//   5. 原版在 initialDirectory 不存在时传 null 给对话框；
//      这里统一先校验目录存在，避免对话框跳到奇怪的位置。

using System;
using System.Windows;
using Microsoft.Win32;

namespace UMango.App.Services;

/// <summary>
/// 基于 WPF 对话框的文件选择实现。
///
/// PCL-CE 用 <c>Microsoft.Win32.OpenFileDialog</c>（它在 WPF 里是包装 Win32 的那一个），
/// 这里沿用同一套类型。
/// </summary>
public sealed class WpfFileDialogHost : IFileDialogHost
{
    private readonly Window? _owner;

    /// <summary>创建对话框宿主。传入 owner 窗口可让对话框正确地显示在其上方。</summary>
    public WpfFileDialogHost(Window? owner = null) => _owner = owner;

    public string[] ShowOpenFiles(FileDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new OpenFileDialog
        {
            AddExtension = true,
            CheckFileExists = true,
            Title = request.Title,
            Filter = request.Filter,
            Multiselect = request.AllowMultiple,
            ValidateNames = true,
        };
        if (!string.IsNullOrEmpty(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;

        return dialog.ShowDialog(_owner) == true ? dialog.FileNames : [];
    }

    public string ShowSaveFile(FileDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            Title = request.Title,
            FileName = request.DefaultFileName,
            Filter = request.Filter,
        };
        if (!string.IsNullOrEmpty(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;

        return dialog.ShowDialog(_owner) == true ? dialog.FileName : "";
    }

    public string ShowOpenFolder(FileDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new OpenFolderDialog
        {
            Title = request.Title,
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(request.InitialDirectory))
            dialog.InitialDirectory = request.InitialDirectory;
        else
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        return dialog.ShowDialog(_owner) == true ? dialog.FolderName : "";
    }
}
