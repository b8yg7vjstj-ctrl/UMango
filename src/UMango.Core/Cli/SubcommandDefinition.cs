// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/SubcommandDefinition.cs。PCL.Core 采用 Apache-2.0 许可，
// 故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：仅命名空间 PCL.Core.App.Cli → UMango.Core.Cli。

namespace UMango.Core.Cli;

/// <summary>
/// 子命令定义。
///
/// 注意：原版刻意**不**描述参数类型、必填项、默认值、帮助文本 ——
/// 参数类型是在解析时按值的文本猜出来的。UMango 沿用这一设计，
/// 保持与 PCL-CE 的行为一致。
/// </summary>
public class SubcommandDefinition
{
    /// <summary>命令文本。</summary>
    public required string CommandText { get; init; }

    /// <summary>下一级子命令。</summary>
    public required IEnumerable<SubcommandDefinition> Subcommands { get; init; }

    /// <summary>子命令名 → 子命令定义 的映射（懒建）。</summary>
    public IReadOnlyDictionary<string, SubcommandDefinition> SubcommandMap
    {
        get
        {
            if (field is not null) return field;
            var map = new Dictionary<string, SubcommandDefinition>();
            foreach (var c in Subcommands) map[c.CommandText] = c;
            return field = map.AsReadOnly();
        }
    } = null!;

    /// <summary>
    /// 是否包含某个子命令名。
    /// </summary>
    public bool Contains(string subcommandText) => SubcommandMap.ContainsKey(subcommandText);

    /// <summary>
    /// 由「命令文本 + 子命令列表」构造。
    /// </summary>
    public static implicit operator SubcommandDefinition(
        (string commandText, IEnumerable<SubcommandDefinition> subcommands) tuple) => new()
    {
        CommandText = tuple.commandText,
        Subcommands = tuple.subcommands
    };

    /// <summary>
    /// 由单个命令文本构造（无子命令）。
    /// </summary>
    public static implicit operator SubcommandDefinition(string commandText) => new()
    {
        CommandText = commandText,
        Subcommands = []
    };
}
