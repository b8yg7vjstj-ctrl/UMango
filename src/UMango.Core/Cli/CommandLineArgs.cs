// SPDX-License-Identifier: Apache-2.0
//
// 本文件重写自 UMango 早期的 UMango.Core/Cli/CommandLineArgs.cs，
// 改为基于 PCL-CE Apache-2.0 的 PCL.Core/App/Cli/ 命令行模型实现。
// PCL.Core 采用 Apache-2.0 许可，详见仓库根目录 LICENSE。

namespace UMango.Core.Cli;

/// <summary>
/// 命令行参数解析（便捷包装）。
///
/// 这是 <see cref="CommandLine"/> 之上的薄封装，为 UMango 内部提供
/// 「按名字取一个字符串 / 布尔 / 整数」的简单用法。
///
/// 为什么不直接用 <see cref="CommandLine"/>：
///   · 内部调用点大多只想问「有没有某个开关」「某个值是什么」，
///     用字典接口比走 <see cref="CommandLine.TryGetArgumentValue{TValue}"/> 直观；
///   · 而命令行**模型**（命令名、子命令树、位置参数）仍然完全由
///     <see cref="CommandLine"/> 提供，两者不重复实现解析规则。
///
/// 相对旧版实现的修正：
///   1. 旧版的文档声称支持 <c>--key value</c>，实现里却没有 ——
///      实际会解析成「<c>--key</c> 是开关」+「<c>value</c> 是位置参数」。
///      现在真的支持了。
///   2. 旧版无法表达「开关为假」：<c>--cache false</c> 里的
///      <c>false</c> 会被当成位置参数，<c>HasFlag("cache")</c> 永远为真。
///      现在布尔值能被正确识别。
///   3. 旧版用 <c>OrdinalIgnoreCase</c> 比较键名（<c>--SOURCE</c> 与
///      <c>--source</c> 等价）。命令行键名**区分大小写**更符合惯例，
///      且与 PCL-CE 保持一致。
/// </summary>
public sealed class CommandLineArgs
{
    private readonly CommandLine _line;

    private CommandLineArgs(CommandLine line) => _line = line;

    /// <summary>参数键 → 值的字典（键区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> Flags
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, arg) in _line.Arguments) dict[key] = arg.ValueText;
            return dict;
        }
    }

    /// <summary>位置参数（裸 token）。</summary>
    public IReadOnlyList<string> Positional => _line.Positional;

    /// <summary>解析出来的命令行模型（需要命令名 / 子命令时用）。</summary>
    public CommandLine Line => _line;

    /// <summary>
    /// 解析命令行参数。
    /// </summary>
    /// <param name="args">原始参数数组（不含可执行文件名）。</param>
    /// <param name="subcommands">已知的子命令定义，可为空。</param>
    public static CommandLineArgs Parse(string[] args, IEnumerable<SubcommandDefinition>? subcommands = null)
    {
        if (args.Length == 0)
            return new CommandLineArgs(CommandLine.ParseOptions([]));

        // 注意用 ParseOptions 而不是 Parse：旧版 CommandLineArgs 没有
        // 「命令名」的概念，第一个元素也只是普通参数/位置参数。
        // Parse 会把 args[0] 吃掉当命令名。
        var line = CommandLine.ParseOptions(args, subcommands);
        return new CommandLineArgs(line);
    }

    /// <summary>尝试获取字符串值。</summary>
    public bool TryGetString(string key, out string value)
    {
        if (_line.Arguments.TryGetValue(key, out var arg))
        {
            value = arg.ValueText;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// 尝试获取布尔标志。
    ///
    /// 只有值为布尔、且为真时才算「有该标志」——
    /// <c>--cache false</c> 不算开启。
    /// </summary>
    public bool HasFlag(string key)
    {
        if (!_line.Arguments.TryGetValue(key, out var arg)) return false;
        if (arg.ValueKind != ArgumentValueKind.Bool) return false;
        return arg.CastValue<bool>();
    }

    /// <summary>尝试获取整数值。</summary>
    public bool TryGetInt(string key, out int value)
    {
        if (_line.Arguments.TryGetValue(key, out var arg) && arg.TryCastValue(out value))
            return true;
        value = 0;
        return false;
    }
}
