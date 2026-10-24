// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/CommandLine.cs。
// PCL.Core 采用 Apache-2.0 许可，故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动（每一处都修掉了原版的一个真实缺陷）：
//   1. 命名空间 PCL.Core.App.Cli → UMango.Core.Cli；
//   2. **位置参数**：原版没有位置参数的概念，裸 token 会被当成参数键
//      （`"1234"` → 键 `"1234"`），于是 "UMango npm install express" 里的
//      npm / install / express 全成了「键」而不是「值」。这里新增
//      <see cref="Positional"/> 收集裸 token，并让 `--` 之后的一切都是位置参数。
//   3. **--key=value**：原版不支持等号形式（`=` 不是特殊字符），
//      这里补上，并让值里可以含 `=`。
//   4. **子命令名不当作参数**：原版在子命令名出现时会递归解析，
//      但没有把子命令名排除出参数表；这里保证子命令名不会被记成参数。
//   5. **重复键**：原版后者覆盖前者且不报错；这里保留**首次**出现
//      （命令行里先写的通常是用户的本意），并提供 <see cref="DuplicateKeys"/>。
//   6. **空参数数组**：原版 `args[0]` 会抛 IndexOutOfRange；
//      这里抛 ArgumentException，并提供 <see cref="TryParse"/>。
//   7. 补 XML 文档；把 JSON 转换器拆到独立文件（CommandLineJsonConverter.cs）。

using System.Text;
using System.Text.Json.Serialization;

namespace UMango.Core.Cli;

/// <summary>
/// 命令行模型。
///
/// 解析规则：
///   · 第一个元素是**主命令**文本；
///   · 后续元素里出现已注册的**子命令名**时，从那里开始递归解析成
///     <see cref="Subcommand"/>；
///   · 形如 <c>--key</c> 的元素是参数键，<c>--key=value</c> 是带值的参数键；
///   · <c>--key value</c> 形式里，value 会被「贪心」地吃掉下一个 token
///     （除非那个 token 是 <c>--</c> 开头的或已注册的子命令名）；
///   · 裸 token 是**位置参数**；
///   · <c>--</c> 之后的一切都是位置参数（转义开关）。
/// </summary>
[JsonConverter(typeof(CommandLineJsonConverter))]
public class CommandLine
{
    /// <summary>命令文本。</summary>
    public required string CommandText { get; init; }

    /// <summary>子命令。</summary>
    public CommandLine? Subcommand { get; init; }

    /// <summary>子命令文本。</summary>
    public string? SubcommandText => Subcommand?.CommandText;

    /// <summary>参数字典。</summary>
    public required IReadOnlyDictionary<string, CommandArgument> Arguments { get; init; }

    /// <summary>
    /// 位置参数（裸 token）。
    ///
    /// 原版没有这个概念 —— 裸 token 会被当成参数键，"npm install express"
    /// 里的三个词全成了「键」。对 UMango 来说这是必须有的：命令行安装
    /// 形如 <c>UMango install express</c>，包名就是位置参数。
    /// </summary>
    public IReadOnlyList<string> Positional { get; init; } = [];

    /// <summary>
    /// 重复出现的参数键（按首次出现顺序）。
    ///
    /// 原版是后者直接覆盖前者，用户写错了也没人知道。这里把重复的记下来，
    /// 让调用方能提示「--source 给了两次」。
    /// </summary>
    public IReadOnlyList<string> DuplicateKeys { get; init; } = [];

    /// <summary>
    /// 尝试获取参数值。
    /// </summary>
    /// <param name="key">参数键。</param>
    /// <param name="value">参数值，若获取失败则为对应类型默认值。</param>
    /// <typeparam name="TValue">参数值的类型。</typeparam>
    /// <returns>
    /// 第一个元素表示是否存在该键；第二个元素表示存在时值的类型是否匹配。
    /// </returns>
    public (bool exists, bool isTypeMatch) TryGetArgumentValue<TValue>(string key, out TValue? value)
    {
        var exists = Arguments.TryGetValue(key, out var arg);
        var isTypeMatch = false;
        if (exists && (isTypeMatch = arg!.TryCastValue(out TValue? typedValue)))
        {
            value = typedValue;
            return (true, true);
        }

        value = default;
        return (exists, isTypeMatch);
    }

    /// <summary>
    /// 解析参数数组，第一个元素会被视为主命令。
    /// </summary>
    /// <param name="args">参数数组。</param>
    /// <param name="subcommands">各级子命令定义。</param>
    /// <returns>命令行模型实例。</returns>
    /// <exception cref="ArgumentException">参数数组为空。</exception>
    public static CommandLine Parse(ReadOnlySpan<string> args, IEnumerable<SubcommandDefinition>? subcommands = null)
    {
        subcommands ??= [];
        if (args.IsEmpty)
            throw new ArgumentException("The argument span must contain at least 1 element", nameof(args));

        SubcommandDefinition root = (args[0], subcommands);
        return CommandLineParser.Parse(args, root);
    }

    /// <summary>
    /// 只解析**参数**，不把第一个元素当作命令名。
    ///
    /// 与 <see cref="Parse"/> 的区别：<see cref="Parse"/> 假定
    /// <c>args[0]</c> 是命令名（PCL-CE 的模型就是这样），
    /// 而只想问「有哪些 --开关 / 位置参数」的调用方并不想丢掉第一个元素。
    /// </summary>
    /// <param name="args">参数数组（全部元素都当作参数，不含命令名）。</param>
    /// <param name="subcommands">已知的子命令名，可为空。</param>
    public static CommandLine ParseOptions(ReadOnlySpan<string> args, IEnumerable<SubcommandDefinition>? subcommands = null)
    {
        if (args.IsEmpty)
            return new CommandLine
            {
                CommandText = string.Empty,
                Arguments = new Dictionary<string, CommandArgument>()
            };

        return CommandLineParser.ParseOptions(args, subcommands ?? []);
    }

    /// <summary>
    /// 尝试解析，失败（参数数组为空）时返回 false 而不抛异常。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<string> args, out CommandLine? commandLine,
        IEnumerable<SubcommandDefinition>? subcommands = null)
    {
        if (args.IsEmpty)
        {
            commandLine = null;
            return false;
        }

        commandLine = Parse(args, subcommands);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CommandText).Append(" [");
        if (Arguments.Count > 0) sb.AppendLine();
        foreach (var arg in Arguments.Values)
        {
            sb.Append("  --").Append(arg.Key);
            var value = arg.ValueKind switch
            {
                ArgumentValueKind.Bool => arg.CastValue<bool>() ? "true" : "false",
                ArgumentValueKind.Decimal => arg.CastValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArgumentValueKind.Text => arg.ValueText,
                _ => null
            };
            if (value is not null) sb.Append(": ").Append(value);
            sb.AppendLine();
        }

        if (Positional.Count > 0)
        {
            sb.Append("  (positional):");
            foreach (var p in Positional) sb.Append(' ').Append(p);
            sb.AppendLine();
        }

        sb.Append(']');
        if (Subcommand is not null) sb.AppendLine().Append("-> ").Append(Subcommand.ToString().Replace("\n", "\n   "));
        return sb.ToString();
    }
}

file static class CommandLineParser
{
    private static (CommandArgument Argument, bool HasValueText) ParseArgument(string key, string possibleValueText)
    {
        if (key.StartsWith("--")) key = key[2..];

        if (possibleValueText.Length == 0 || possibleValueText.StartsWith("--"))
            return (new BoolArgument { Key = key, ValueText = string.Empty }, false);

        // 布尔：显式的真假词都认，避免 "--no-cache" 被读成 true。
        // 注意**不把 "0" / "1" 当布尔** —— 它们先要到下面的数字分支，
        // 否则 "--limit 1" 会被读成布尔真，参数类型就乱了。
        if (possibleValueText.Trim().ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off"
            or "enable" or "enabled" or "disable" or "disabled")
            return (new BoolArgument { Key = key, ValueText = possibleValueText }, true);

        // 只允许「可选符号 + 小数点 + 数字」，**不允许千分位逗号** ——
        // 命令行里的 "1,5" 应当是无效的，而不是被读成 15 或 1.5。
        if (decimal.TryParse(possibleValueText,
                System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (new DecimalArgument { Key = key, ValueText = possibleValueText, Value = d }, true);

        return (new TextArgument { Key = key, ValueText = possibleValueText }, true);
    }

    /// <summary>
    /// 解析一组参数（不含命令名）。
    /// </summary>
    public static CommandLine ParseOptions(ReadOnlySpan<string> args, IEnumerable<SubcommandDefinition> subcommands)
    {
        var root = new SubcommandDefinition { CommandText = string.Empty, Subcommands = subcommands };
        return ParseCore(args, root, 0, string.Empty, greedyValues: false);
    }

    public static CommandLine Parse(ReadOnlySpan<string> args, SubcommandDefinition subcommands)
    {
        if (args.IsEmpty) throw new ArgumentException("The argument span must contain at least 1 element", nameof(args));

        return ParseCore(args, subcommands, 1, args[0]);
    }

    /// <summary>
    /// 解析主体。
    /// </summary>
    /// <param name="args">完整参数数组。</param>
    /// <param name="subcommands">当前层已知的子命令。</param>
    /// <param name="startIndex">从第几个元素开始读（0 = 全部元素都是参数）。</param>
    /// <param name="commandText">本层的命令文本。</param>
    private static CommandLine ParseCore(ReadOnlySpan<string> args, SubcommandDefinition subcommands,
        int startIndex, string commandText, bool greedyValues = true)
    {
        var i = startIndex;
        var argumentList = new Dictionary<string, CommandArgument>();
        var duplicateKeys = new List<string>();
        var positional = new List<string>();
        CommandLine? subcommand = null;
        var escaped = false;

        while (i < args.Length)
        {
            var currentText = args[i];

            // 「--」之后的一切都是位置参数。
            if (!escaped && currentText == "--")
            {
                escaped = true;
                i++;
                continue;
            }

            if (!escaped && subcommands.Contains(currentText))
            {
                subcommand = ParseCore(args, subcommands.SubcommandMap[currentText], i + 1, currentText);
                break;
            }

            if (escaped || !currentText.StartsWith("--"))
            {
                // 裸 token → 位置参数。
                positional.Add(currentText);
                i++;
                continue;
            }

            // --key=value：等号后的整段都是值（值里可以再含等号）
            var eq = currentText.IndexOf('=');
            if (eq > 2)
            {
                var key = currentText[2..eq];
                var valueText = currentText[(eq + 1)..];
                var (eqArgument, _) = ParseArgument(key, valueText);
                if (argumentList.ContainsKey(eqArgument.Key)) duplicateKeys.Add(eqArgument.Key);
                else argumentList[eqArgument.Key] = eqArgument;
                i++;
                continue;
            }

            // --key [value]：值会被贪心吃掉下一个 token。
            //
            // **只有命令模型模式才贪心**。在「纯参数」模式（ParseOptions）下
            // 关掉贪心，因为那里没有命令名可依赖，`--debug install` 到底是
            // 「debug 的值是 install」还是「debug 是开关、install 是位置参数」
            // 无法判定 —— 贪心会让 `--debug` 悄悄吞掉第一个位置参数。
            // 纯参数模式下值一律用 `--key=value` 给出。
            var nextIsValue = greedyValues
                              && i + 1 < args.Length
                              && !args[i + 1].StartsWith("--")
                              && !subcommands.Contains(args[i + 1]);
            var (argument, hasValueText) = ParseArgument(currentText, nextIsValue ? args[i + 1] : string.Empty);

            if (argumentList.ContainsKey(argument.Key)) duplicateKeys.Add(argument.Key);
            else argumentList[argument.Key] = argument;

            i += hasValueText ? 2 : 1;
        }

        return new CommandLine
        {
            CommandText = commandText,
            Arguments = argumentList.AsReadOnly(),
            Subcommand = subcommand,
            Positional = positional.AsReadOnly(),
            DuplicateKeys = duplicateKeys.AsReadOnly()
        };
    }
}
