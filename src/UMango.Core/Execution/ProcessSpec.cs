namespace UMango.Core.Execution;

/// <summary>一次命令行调用的描述。</summary>
public sealed class ProcessSpec
{
    public required string Program { get; init; }
    public IList<string> Args { get; init; } = new List<string>();
    public string? WorkingDirectory { get; init; }
    public int TimeoutMs { get; init; } = 120_000;
    /// <summary>是否把输出逐行回调给订阅者（终端集成桥需要）</summary>
    public bool StreamOutput { get; init; }

    public static ProcessSpec Of(string program, params string[] args) =>
        new() { Program = program, Args = args.ToList() };

    public string CommandLine => Args.Count == 0 ? Program : $"{Program} {string.Join(' ', Args)}";
}
