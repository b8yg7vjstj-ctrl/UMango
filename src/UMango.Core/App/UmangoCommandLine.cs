// SPDX-License-Identifier: Apache-2.0
//
// 本文件基于 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/ 命令行模型重写。PCL.Core 采用 Apache-2.0 许可，
// 故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.App.Cli → UMango.Core.Cli（模型本身在 Core/Cli/）；
//   2. 原版只提供模型，命令定义在它自己的 StartupService（IoC/RPC）里；
//      UMango 不引入那套 IoC 体系，只保留模型并在自己的 App 层定义命令。

using UMango.Core.Cli;

namespace UMango.Core.App;

/// <summary>
/// UMango 自己的命令行协议。
///
/// 与 PCL-CE 的关系：PCL-CE 的 <c>App/Cli/</c> 提供**通用模型**
/// （命令名 + 子命令树 + 参数，参数类型在解析时按值文本推断），
/// 而实际命令的定义、参数说明和处理逻辑都放在它自己的
/// <c>StartupService</c>（IoC/RPC）里。UMango 保留模型、另写命令层。
///
/// 与自更新助手协议的共存（重要）：
///   · 自更新助手用的是**位置参数**协议
///     （<c>update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;restart&gt;</c>），
///     解析逻辑在 <see cref="UpdateCommandLine"/>，严格 Ordinal、精确元数；
///   · 本协议把 <c>update</c> / <c>update_finished</c> / <c>update_failed</c>
///     **登记为保留词**，绝不让它们出现在子命令表里 ——
///     否则 <c>update 1234 target source true</c> 会被命令模型当成
///     「子命令 update + 位置参数」，与助手协议的解析结果不一致；
///   · 入口处的顺序是：先判更新助手
///     （<see cref="UpdateCommandLine.IsUpdateCommand"/>），再走本协议。
/// </summary>
public static class UmangoCommandLine
{
    /// <summary>
    /// 更新助手协议占用的词。**不出现在任何子命令表里**。
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedCommandNames =
    [
        "update",
        "update_finished",
        "update_failed"
    ];

    /// <summary>
    /// UMango 支持的顶层命令。
    ///
    /// 刻意**不描述参数类型、必填项、默认值、帮助文本** ——
    /// 这是 PCL-CE 原版模型的设计（参数类型在解析时按值文本猜），
    /// 保持一致以便行为可预期。
    /// </summary>
    public static readonly IReadOnlyList<SubcommandDefinition> Subcommands = BuildSubcommands();

    private static IReadOnlyList<SubcommandDefinition> BuildSubcommands()
    {
        var names = new[] { "install", "uninstall", "upgrade", "search", "list", "source", "rpc", "help" };
        var list = new List<SubcommandDefinition>(names.Length);
        foreach (var name in names)
        {
            // 保留词不进表：自更新助手协议优先于本协议解析。
            if (IsReserved(name)) continue;
            list.Add(new SubcommandDefinition { CommandText = name, Subcommands = [] });
        }

        return list;
    }

    /// <summary>
    /// 解析命令行。
    ///
    /// **保留词一律不解析** —— 若 <c>args[0]</c> 是自更新助手的命令词
    /// （<c>update</c> / <c>update_finished</c> / <c>update_failed</c>），
    /// 直接返回 false，交给 <see cref="UpdateCommandLine"/> 处理。
    ///
    /// 为什么不只是把它们从子命令表里删掉：助手协议的位置参数里
    /// 含有字面量 <c>source</c>（target 与 source 路径），
    /// 而 UMango 自己也有一个叫 <c>source</c> 的子命令 ——
    /// 助手协议的 <c>update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;restart&gt;</c>
    /// 会被命令模型把 <c>source</c> 吃成子命令，解析结果与助手协议不一致。
    /// 所以必须在入口处就分流。
    /// </summary>
    /// <param name="args">原始参数数组（不含可执行文件名）。</param>
    /// <param name="commandLine">解析结果。</param>
    /// <returns>解析是否成功；参数数组为空、或首个词是保留词时返回 false。</returns>
    public static bool TryParse(string[] args, out CommandLine? commandLine)
    {
        if (args.Length == 0 || IsReserved(args[0]))
        {
            commandLine = null;
            return false;
        }

        commandLine = CommandLine.Parse(args, Subcommands);
        return true;
    }

    /// <summary>
    /// 取得第一个位置参数（包名 / 关键词）。
    /// </summary>
    public static string? FirstPositional(CommandLine? commandLine)
    {
        if (commandLine is null) return null;
        foreach (var p in commandLine.Positional)
        {
            if (p.Length > 0) return p;
        }

        return null;
    }

    /// <summary>
    /// 判断某个词是否是保留词（自更新助手专用）。
    /// </summary>
    public static bool IsReserved(string commandText) =>
        ReservedCommandNames.Contains(commandText, StringComparer.Ordinal);
}
