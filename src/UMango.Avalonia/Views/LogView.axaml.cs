// SPDX-License-Identifier: Apache-2.0
//
// Avalonia 侧的日志页 —— 与 WPF 版（`src/UMango.App/Views/LogView.xaml.cs`）同构。
//
// 代码后置只做一件事：把自己的 ViewModel 装上。
// 业务逻辑全在 `UMango.Shell.ViewModels.LogViewModel` 里，
// 那部分不依赖任何 UI 框架，因此可以在 macOS 上被单元测试覆盖。

using Avalonia.Controls;
using UMango.Shell.ViewModels;

namespace UMango.Avalonia.Views;

/// <summary>日志页。参数less 构造：外壳直接 new 出来放进内容区。</summary>
public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContext = new LogViewModel();
    }
}
