using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UMango.Core.Execution;
using UMango.Core.Localization;

namespace UMango.App.ViewModels;

/// <summary>终端输出行的类型（决定颜色与是否加粗）。</summary>
public enum TerminalEntryKind
{
    /// <summary>用户输入的命令回显（以 $ 开头）。</summary>
    Command,
    /// <summary>标准输出。</summary>
    Output,
    /// <summary>标准错误。</summary>
    Error,
    /// <summary>本程序插入的提示（开始/结束/取消）。</summary>
    Info,
}

/// <summary>终端里的一行。</summary>
public sealed class TerminalEntry
{
    public required string Text { get; init; }
    public TerminalEntryKind Kind { get; init; }
    public bool IsError => Kind == TerminalEntryKind.Error;
    public bool IsInfo => Kind == TerminalEntryKind.Info;
    public bool IsCommand => Kind == TerminalEntryKind.Command;
}

/// <summary>
/// 终端页的 ViewModel：把用户输入的命令交给 <see cref="ProcessRunner"/> 执行，
/// 并把它逐行回传的输出显示出来。
///
/// <para>
/// 为什么放在 App 而不是 Shell：这是 WPF 壳独有的终端页（Avalonia 壳不提供），
/// 按项目约定「只开发 WPF」，共享层不引入只为它服务的类型。
/// </para>
///
/// <para>
/// **不是终端模拟器**：没有交互式输入、没有光标控制。它和进度页的「终端集成桥」
/// 是同一种东西 —— 可观测的流式输出。
/// </para>
/// </summary>
public partial class TerminalViewModel : ObservableObject
{
    /// <summary>内存里保留的最大行数（超出后从最早的行开始丢）。</summary>
    private const int Capacity = 5000;

    /// <summary>单条命令的等待上限（30 分钟）——终端里跑长任务不该被默认的 2 分钟打断。</summary>
    private const int TimeoutMs = 30 * 60 * 1000;

    private CancellationTokenSource? _cts;

    /// <summary>全部输出（导出时用它，不受过滤影响）。</summary>
    public ObservableCollection<TerminalEntry> Lines { get; } = [];

    /// <summary>当前过滤条件下实际显示的行。</summary>
    public ObservableCollection<TerminalEntry> VisibleLines { get; } = [];

    [ObservableProperty]
    private string _command = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _filter = "";

    /// <summary>有新行时触发（视图据此自动滚到底）。</summary>
    public event Action? LineAppended;

    private static string L(string key, params object?[] args) => LocalizationService.Text(key, args);

    partial void OnFilterChanged(string value) => RebuildVisibleLines();

    [RelayCommand]
    private async Task RunAsync()
    {
        var command = Command.Trim();
        if (command.Length == 0 || IsRunning) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Append(TerminalEntryKind.Command, "$ " + command);
        StatusMessage = L("Terminal.Running", command);

        try
        {
            var result = await ProcessRunner.RunAsync(
                BuildSpec(command),
                (isStdErr, line) => Append(isStdErr ? TerminalEntryKind.Error : TerminalEntryKind.Output, line),
                _cts.Token);

            var text = result.TimedOut
                ? L("Terminal.Cancelled", command)
                : L("Terminal.Done", command, result.DurationMs / 1000.0, result.ExitCode);

            Append(TerminalEntryKind.Info, text);
            StatusMessage = text;
        }
        catch (OperationCanceledException)
        {
            var text = L("Terminal.Cancelled", command);
            Append(TerminalEntryKind.Info, text);
            StatusMessage = text;
        }
        catch (Exception ex)
        {
            // 命令不存在 / 无法启动：把原始信息留在输出区，用户能直接看到原因
            Append(TerminalEntryKind.Error, ex.Message);
            StatusMessage = L("Terminal.Failed", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        Lines.Clear();
        VisibleLines.Clear();
        StatusMessage = null;
        LineAppended?.Invoke();
    }

    [RelayCommand]
    private void Export()
    {
        try
        {
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(directory, $"umango-terminal-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            using var writer = new StreamWriter(path);
            foreach (var line in Lines) writer.WriteLine(line.Text);

            StatusMessage = L("Terminal.Exported", path);
        }
        catch (Exception ex)
        {
            StatusMessage = L("Terminal.ExportFailed", ex.Message);
        }
    }

    /// <summary>按当前过滤串重建可见行（空串 = 全部显示）。</summary>
    private void RebuildVisibleLines()
    {
        VisibleLines.Clear();
        var keyword = Filter.Trim();
        foreach (var line in Lines)
        {
            if (Matches(line, keyword)) VisibleLines.Add(line);
        }
    }

    private void Append(TerminalEntryKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var entry = new TerminalEntry { Text = text, Kind = kind };
        Lines.Add(entry);
        if (Matches(entry, Filter.Trim())) VisibleLines.Add(entry);

        if (Lines.Count > Capacity)
        {
            var overflow = Lines.Count - Capacity;
            for (var i = 0; i < overflow; i++)
            {
                var oldest = Lines[0];
                Lines.RemoveAt(0);
                VisibleLines.Remove(oldest);
            }
        }

        LineAppended?.Invoke();
    }

    private static bool Matches(TerminalEntry entry, string keyword)
        => keyword.Length == 0 || entry.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按当前平台选一个 shell —— 引号、管道、通配符都由 shell 解释，用户写的命令更自然。
    /// </summary>
    private static ProcessSpec BuildSpec(string command) => OperatingSystem.IsWindows()
        ? CreateSpec("cmd.exe", "/c", command)
        : CreateSpec("/bin/sh", "-lc", command);

    private static ProcessSpec CreateSpec(string program, string flag, string command) => new()
    {
        Program = program,
        Args = [flag, command],
        TimeoutMs = TimeoutMs,
    };
}
