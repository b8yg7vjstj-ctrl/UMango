// SPDX-License-Identifier: Apache-2.0
//
// 子项视图行：一个 Core 子项（TaskModel）+ 它自己的终端输出。
//
// 这一层从 WPF 壳（src/UMango.App/Views/ProgressView.xaml.cs）下沉到 Shell，
// 让 WPF 壳与 Avalonia 壳共用同一份实现 —— 需求里的「降低重复代码率」。
//
// 为什么需要这一层（而不是直接把 TerminalGroups 绑到子项行上）：
//   1. TerminalGroups 只包含"已经有输出"的子项，而需求要求**每个子项都有独立进度条**，
//      两者条目数不一致，直接绑会让没有输出的子项在界面上消失；
//   2. 把输出挂到"它所属的那一行下面"需要按子项标题即时分发，并且要处理
//      清空（Reset）/调试模式切换后的重建；
//   3. 进度/状态/中文名仍然直接绑定 Child（Core 对象），这里**不复制**任何业务数据，
//      只镜像"输出行"这一份展示数据。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UMango.Core.App.Tasks;
using UMango.Core.Localization;
using UMango.Core.Terminal;

namespace UMango.Shell.ViewModels;

/// <summary>
/// 子项视图行：一个 Core 子项（<see cref="TaskModel"/>）+ 它自己的终端输出。
/// </summary>
public sealed class StepRow : ObservableObject
{
    private string? _truncationHint;
    private int _totalLines;

    /// <summary>构造一行。</summary>
    public StepRow(TaskModel child) => Child = child;

    /// <summary>对应的 Core 子项：中文名、进度、状态都来自它。</summary>
    public TaskModel Child { get; }

    /// <summary>该子项的终端输出（时间正序，超出界面上限时从头裁剪）。</summary>
    public ObservableCollection<TerminalLine> Output { get; } = [];

    /// <summary>「已省略 N 行」提示；没有被省略时为 null（界面折叠该行）。</summary>
    public string? TruncationHint
    {
        get => _truncationHint;
        private set => SetProperty(ref _truncationHint, value);
    }

    /// <summary>追加一行并按上限裁剪（调试模式传 int.MaxValue 表示不裁剪）。</summary>
    public void Append(TerminalLine line, int limit)
    {
        _totalLines++;
        Output.Add(line);
        while (Output.Count > limit) Output.RemoveAt(0);
        UpdateHint();
    }

    /// <summary>重新计数（整体重建输出前调用）。</summary>
    public void Reset()
    {
        _totalLines = 0;
        Output.Clear();
        TruncationHint = null;
    }

    private void UpdateHint()
    {
        var omitted = _totalLines - Output.Count;
        TruncationHint = omitted > 0
            ? LocalizationService.Text("Progress.EarlierOmitted", omitted)
            : null;
    }
}
