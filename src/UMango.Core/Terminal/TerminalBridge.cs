using System.Collections.Concurrent;

namespace UMango.Core.Terminal;

/// <summary>
/// 终端集成桥 —— 把命令行工具的输出**原生回传**到图形界面。
///
/// 设计目标（对应需求原文）：
///   「做一个终端集成桥，实现可观测的输出。在 PCL-CE 中原菜单位置打开调试模式后
///     可在下载进度页原生回传终端输出，而非以黑底硬嵌终端，与子中文进度同级。」
///
/// 因此这里刻意**不是**一个终端模拟器：
///   · 不做 ANSI 渲染、不维护行列光标、不嵌黑底控件；
///   · 只把「结构化的一行行输出」交给界面，由界面用普通文本呈现；
///   · 每一行都带 <see cref="TerminalLine.Step"/>，界面据此把输出**挂在对应子项下方**，
///     与中文进度文案同级显示；
///   · 调试模式关闭时默认只保留最近若干行（避免长安装把内存与界面刷爆），
///     打开调试模式才保留完整历史并逐行上屏。
///
/// 线程安全：CLI 的 stdout/stderr 回调来自不同线程，这里全部走锁保护。
/// </summary>
public sealed class TerminalBridge
{
    /// <summary>非调试模式下每个通道保留的最大行数</summary>
    private const int DefaultCapacity = 200;

    /// <summary>调试模式下保留的最大行数（放宽但仍有上限）</summary>
    private const int DebugCapacity = 5000;

    private static readonly ConcurrentDictionary<string, TerminalBridge> Channels = new();

    private readonly List<TerminalLine> _lines = [];
    private readonly Lock _gate = new();

    /// <summary>当前步骤名。CLI 输出会被归到这个步骤下。</summary>
    public string? CurrentStep { get; private set; }

    /// <summary>新增一行时触发。界面订阅它做实时刷新。</summary>
    public event Action<TerminalLine>? LineAppended;

    /// <summary>行被清空时触发。</summary>
    public event Action? Cleared;

    /// <summary>
    /// 调试模式。开启后：
    ///   · 保留更多历史行；
    ///   · 界面应把终端输出与子项进度**同级**展开显示。
    /// 由设置页的「调试模式」开关控制（原 PCL-CE 的菜单位置）。
    /// </summary>
    public static bool DebugMode { get; set; }

    /// <summary>取（或创建）某个通道。安装任务以任务标题为通道名。</summary>
    public static TerminalBridge For(string channelId)
    {
        var key = string.IsNullOrWhiteSpace(channelId) ? "default" : channelId;
        return Channels.GetOrAdd(key, _ => new TerminalBridge());
    }

    /// <summary>移除通道（任务结束且用户清空后调用）。</summary>
    public static void Remove(string channelId) => Channels.TryRemove(channelId, out _);

    /// <summary>当前全部通道名（诊断页展示用）。</summary>
    public static IReadOnlyCollection<string> ChannelNames => Channels.Keys.ToList();

    /// <summary>历史行快照（时间正序）。</summary>
    public IReadOnlyList<TerminalLine> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>把当前输出归到某个安装步骤下（与中文子项同级）。</summary>
    public void BeginStep(string? step)
    {
        lock (_gate) CurrentStep = step;
    }

    /// <summary>写入一行（CLI 的 stdout/stderr 回调直接调它）。</summary>
    public void Write(bool isStdErr, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        TerminalLine line;
        lock (_gate)
        {
            line = new TerminalLine
            {
                At = DateTimeOffset.Now,
                IsStdErr = isStdErr,
                Text = text.TrimEnd(),
                Step = CurrentStep
            };
            _lines.Add(line);
            var cap = DebugMode ? DebugCapacity : DefaultCapacity;
            if (_lines.Count > cap) _lines.RemoveRange(0, _lines.Count - cap);
        }

        // 在锁外触发事件，避免订阅者回调里再访问本对象造成死锁
        LineAppended?.Invoke(line);
    }

    /// <summary>便捷方法：作为 <see cref="Execution.ProcessRunner.OutputLineHandler"/> 直接传入。</summary>
    public void OnProcessLine(bool isStdErr, string line) => Write(isStdErr, line);

    /// <summary>清空本通道。</summary>
    public void Clear()
    {
        lock (_gate) _lines.Clear();
        Cleared?.Invoke();
    }

    /// <summary>只取某个步骤的输出（界面按子项分组渲染时用）。</summary>
    public IReadOnlyList<TerminalLine> LinesOfStep(string step)
    {
        lock (_gate) return _lines.Where(l => l.Step == step).ToList();
    }
}
