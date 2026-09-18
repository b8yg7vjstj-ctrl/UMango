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

    /// <summary>
    /// 追加/覆盖的环境变量。
    /// 用途举例：调用 aria2 时设 LC_ALL=C —— 它的消息是**本地化**的
    /// （中文环境输出「下载完成：」），强制英文才能得到稳定可解析的输出。
    /// </summary>
    public IList<(string Key, string Value)> Env { get; init; } = new List<(string, string)>();

    public static ProcessSpec Of(string program, params string[] args) =>
        new() { Program = program, Args = args.ToList() };

    public string CommandLine => Args.Count == 0 ? Program : $"{Program} {string.Join(' ', Args)}";
}
