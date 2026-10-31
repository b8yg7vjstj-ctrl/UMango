// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧下载器页 —— 与 WPF 版（`src/UMango.App/Views/DownloaderView.xaml.cs`）同构。
//
// 代码后置只做两件事：装 ViewModel、接「浏览输出目录」按钮。
// 文件夹选择对话框是 Avalonia 类型，不能放进 ViewModel（那会让 Shell/Core
// 依赖 UI 框架，破坏「Core 纯 .NET、能在 net10.0 支编译」的约定）。

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>下载器页。</summary>
public partial class DownloaderView : UserControl
{
    public DownloaderView()
    {
        InitializeComponent();
        DataContext = new DownloaderViewModel();
    }

    /// <summary>「浏览…」按钮：选一个目录写回 ViewModel。</summary>
    private async void BrowseOutputDirectory_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DownloaderViewModel vm)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;

        // StorageProvider 是 Avalonia 抽象，跨平台；用 folder picker 而不是
        // 平台分支 —— macOS/Linux/Windows 各自有实现。
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
            vm.OutputDirectory = folders[0].TryGetLocalPath() ?? folders[0].Name;
    }
}
