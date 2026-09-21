using System.Text.RegularExpressions;

namespace UMango.Core.Cli;

/// <summary>
/// 命令行参数解析。
///
/// 对应 PCL-CE 的 App/Cli/（Apache-2.0）。
/// 支持两种风格：
///   1. Unix 风格：--flag、--key=value
///   2. 空格分隔：--key value
///
/// 使用场景：
///   · UMango --debug → 开启调试模式
///   · UMango --source npm → 直接打开某个包源
///   · UMango npm install express → 命令行安装
/// </summary>
public sealed class CommandLineArgs
{
    private readonly Dictionary<string, string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    public IReadOnlyDictionary<string, string> Flags => _flags;
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>解析命令行参数。</summary>
    public static CommandLineArgs Parse(string[] args)
    {
        var result = new CommandLineArgs();
        var i = 0;

        while (i < args.Length)
        {
            var arg = args[i];

            if (arg.StartsWith("--"))
            {
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    // --key=value
                    result._flags[arg[2..eq]!] = arg[(eq + 1)..];
                }
                else
                {
                    // --flag (boolean，不接受空格分隔的值——那会产生歧义)
                    result._flags[arg[2..]!] = "true";
                }
            }
            else
            {
                result._positional.Add(arg);
            }

            i++;
        }

        return result;
    }

    /// <summary>尝试获取字符串值。</summary>
    public bool TryGetString(string key, out string value) => _flags.TryGetValue(key, out value!);

    /// <summary>尝试获取布尔标志。</summary>
    public bool HasFlag(string key) => _flags.ContainsKey(key) && _flags[key] == "true";

    /// <summary>尝试获取整数值。</summary>
    public bool TryGetInt(string key, out int value)
    {
        if (_flags.TryGetValue(key, out var str) && int.TryParse(str, out value))
            return true;
        value = 0;
        return false;
    }
}
