// SPDX-License-Identifier: Apache-2.0
//
// 本文件的算法思路参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Controls/SvgIcon/SvgPathGeometryParser.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 → UMango.Core.UI.Svg；
//   2. **剥离 WPF 依赖**：原版直接产出 System.Windows.Media.Geometry，
//      UMango 改为产出一套平台中立的路径段模型（SvgPathFigure / SvgPathSegment），
//      由 App 层的 WPF 适配器再转成 Geometry。
//      这样路径解析可以在 net10.0 支编译、在 macOS 上被单元测试覆盖；
//   3. 原版的分词器是私有嵌套类，这里提取为独立的 internal 类型，
//      便于单独测试「分词」与「执行」两层。

using System.Globalization;
using System.Text.RegularExpressions;

namespace UMango.Core.UI.Svg;

/// <summary>SVG 路径命令。</summary>
internal enum SvgPathCommand
{
    MoveTo,
    LineTo,
    HorizontalLineTo,
    VerticalLineTo,
    CubicBezierTo,
    SmoothCubicBezierTo,
    QuadraticBezierTo,
    SmoothQuadraticBezierTo,
    ArcTo,
    CloseFigure,
}

/// <summary>路径分词结果的一种。</summary>
internal enum SvgPathTokenKind
{
    Command,
    Number,
}

/// <summary>一个分词结果。</summary>
internal readonly record struct SvgPathToken(SvgPathTokenKind Kind, char Command, double Number);

/// <summary>
/// SVG 路径数据的分词器。
///
/// 负责把 <c>d="M10 10 L20 20 …"</c> 这样的字符串切成「命令 + 数字」序列，
/// 不解释语义（语义在 <see cref="SvgPathBuilder"/> 里）。
/// </summary>
internal static partial class SvgPathTokenizer
{
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    /// <summary>把路径数据切分成词元序列。</summary>
    public static IReadOnlyList<SvgPathToken> Tokenize(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var tokens = new List<SvgPathToken>();
        var index = 0;

        while (index < data.Length)
        {
            var ch = data[index];
            if (char.IsWhiteSpace(ch) || ch == ',')
            {
                index++;
                continue;
            }

            if (TryMapCommand(ch, out var command))
            {
                tokens.Add(new SvgPathToken(SvgPathTokenKind.Command, command, 0D));
                index++;
                continue;
            }

            var start = index;
            if (ch is '+' or '-')
                index++;

            var hasDigit = false;
            while (index < data.Length && char.IsDigit(data[index]))
            {
                index++;
                hasDigit = true;
            }

            if (index < data.Length && data[index] == '.')
            {
                index++;
                while (index < data.Length && char.IsDigit(data[index]))
                {
                    index++;
                    hasDigit = true;
                }
            }

            if (!hasDigit)
                throw new FormatException($"Invalid SVG path number near index {start}.");

            if (index < data.Length && data[index] is 'e' or 'E')
            {
                var exponentStart = index;
                index++;
                if (index < data.Length && data[index] is '+' or '-')
                    index++;

                var exponentHasDigit = false;
                while (index < data.Length && char.IsDigit(data[index]))
                {
                    index++;
                    exponentHasDigit = true;
                }

                if (!exponentHasDigit)
                    index = exponentStart;
            }

            var numberText = data[start..index];
            tokens.Add(new SvgPathToken(
                SvgPathTokenKind.Number,
                '\0',
                double.Parse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture)));
        }

        return tokens;
    }

    /// <summary>把字符映射成命令；不是命令字符时返回 false。</summary>
    public static bool TryMapCommand(char ch, out char command)
    {
        switch (ch)
        {
            case 'M': command = 'M'; return true;
            case 'm': command = 'm'; return true;
            case 'Z': command = 'Z'; return true;
            case 'z': command = 'z'; return true;
            case 'L': command = 'L'; return true;
            case 'l': command = 'l'; return true;
            case 'H': command = 'H'; return true;
            case 'h': command = 'h'; return true;
            case 'V': command = 'V'; return true;
            case 'v': command = 'v'; return true;
            case 'C': command = 'C'; return true;
            case 'c': command = 'c'; return true;
            case 'S': command = 'S'; return true;
            case 's': command = 's'; return true;
            case 'Q': command = 'Q'; return true;
            case 'q': command = 'q'; return true;
            case 'T': command = 'T'; return true;
            case 't': command = 't'; return true;
            case 'A': command = 'A'; return true;
            case 'a': command = 'a'; return true;
            default: command = '\0'; return false;
        }
    }
}
