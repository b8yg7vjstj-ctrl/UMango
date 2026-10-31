// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的音乐页 —— 与 WPF 版（`src/UMango.App/Views/MusicView.xaml.cs`）同构。
//
// 代码后置只做两件事：把自己的 ViewModel 装上、在 Attached 时触发一次曲库扫描。
// 业务逻辑全在 `UMango.Shell.ViewModels.MusicViewModel` 与
// `UMango.Core.Media.Music` 里，那部分可以在 macOS 上被单元测试覆盖。

using Avalonia.Controls;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>音乐页。参数less 构造：外壳直接 new 出来放进内容区。</summary>
public partial class MusicView : UserControl
{
    public MusicView()
    {
        InitializeComponent();
        DataContext = new MusicViewModel();
    }

    /// <summary>首次挂到视觉树时扫一次曲库（对应 WPF 侧的 Loaded 事件）。</summary>
    protected override void OnAttachedToVisualTree(global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is MusicViewModel vm)
            vm.RefreshCommand.Execute(null);
    }
}
