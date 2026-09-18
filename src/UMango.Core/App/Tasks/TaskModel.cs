// SPDX-License-Identifier: Apache-2.0
// 采用并改造自 PCL-CE（PCL.Core/App/Tasks/TaskModel.cs，Apache-2.0）。
// 改造点：加上 IsGroup/Children 以承载子项进度；补充包管理器语义的注释。
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UMango.Core.App.Tasks;

/// <summary>
/// 任务的界面模型。所有属性都可观察，界面直接绑定。
/// </summary>
public partial class TaskModel : ObservableObject
{
    /// <summary>任务标题</summary>
    public required string Title { get; init; }

    /// <summary>是否支持进度显示（不支持时界面显示不确定态进度条）</summary>
    public required bool SupportProgress { get; init; }

    [ObservableProperty] private TaskState _state = TaskState.Waiting;
    [ObservableProperty] private string _stateMessage = string.Empty;
    [ObservableProperty] private double _progress;

    private static readonly Action EmptyAction = static () => { };

    /// <summary>取消回调；为 null 时界面上的取消按钮不可用</summary>
    public required Action? OnCancel { private get; init; }

    public RelayCommand Cancel =>
        field ??= new RelayCommand(OnCancel ?? EmptyAction, () => OnCancel is not null);

    /// <summary>暂停回调；为 null 时界面上的暂停按钮不可用</summary>
    public required Action? OnPause { private get; init; }

    public RelayCommand Pause =>
        field ??= new RelayCommand(OnPause ?? EmptyAction, () => OnPause is not null);

    /// <summary>是否为任务组（有子项）</summary>
    [ObservableProperty] private bool _isGroup;

    /// <summary>
    /// 子任务。**这就是「按下载方式列明子项进度」的数据结构**：
    /// 父任务一次安装 → 子项按阶段/下载方式展开，各自带进度。
    /// </summary>
    public ObservableCollection<TaskModel> Children { get; } = [];

    /// <summary>创建一个不报告进度的简单任务模型。</summary>
    public static TaskModel Simple(string title, Action? onCancel = null, Action? onPause = null) =>
        new() { Title = title, SupportProgress = false, OnCancel = onCancel, OnPause = onPause };

    /// <summary>创建一个可报告进度的任务模型。</summary>
    public static TaskModel Progressive(string title, Action? onCancel = null, Action? onPause = null) =>
        new() { Title = title, SupportProgress = true, OnCancel = onCancel, OnPause = onPause };

    /// <summary>创建一个任务组模型（有子项）。</summary>
    public static TaskModel Group(string title, Action? onCancel = null, Action? onPause = null) =>
        new()
        {
            Title = title, SupportProgress = true, IsGroup = true,
            OnCancel = onCancel, OnPause = onPause
        };
}
