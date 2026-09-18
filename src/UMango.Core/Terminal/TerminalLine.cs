namespace UMango.Core.Terminal;

/// <summary>终端输出的一行。</summary>
public sealed class TerminalLine
{
    public required DateTimeOffset At { get; init; }
    public required bool IsStdErr { get; init; }
    public required string Text { get; init; }
    /// <summary>该行归属于哪个安装步骤（空表示尚未绑定到具体子项）</summary>
    public string? Step { get; init; }

    public override string ToString() =>
        $"[{At:HH:mm:ss}] {(IsStdErr ? "ERR " : "")}{Text}";
}
