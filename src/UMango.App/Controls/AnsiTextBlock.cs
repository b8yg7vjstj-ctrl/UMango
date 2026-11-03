using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace UMango.App.Controls;

/// <summary>
/// 支持 ANSI SGR 颜色码的文本块。
///
/// <para>
/// 为什么需要：CLI 的输出里常带 <c>\x1B[32m…\x1B[0m</c> 这类颜色码，
/// 直接塞进 <see cref="TextBlock.Text"/> 会把转义序列当普通字符画出来（界面上出现 ^[[32m）。
/// 本控件把一行文本解析成若干「同色片段」，用 <see cref="Run"/> 拼进去。
/// </para>
///
/// <para>
/// 只处理 SGR（颜色/加粗）：光标移动、清屏等其它 CSI 序列一律**剥离**，不做光标模拟 ——
/// 这不是终端模拟器，只是一块能正确显示颜色的输出区。非 SGR 序列若原样保留反而会污染文本。
/// </para>
/// </summary>
public class AnsiTextBlock : TextBlock
{
    public static readonly DependencyProperty AnsiProperty = DependencyProperty.Register(
        nameof(Ansi),
        typeof(string),
        typeof(AnsiTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((AnsiTextBlock)d).Rebuild()));

    /// <summary>带 ANSI 转义序列的原始文本。</summary>
    public string Ansi
    {
        get => (string)GetValue(AnsiProperty);
        set => SetValue(AnsiProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();

        foreach (var run in AnsiParser.Parse(Ansi ?? string.Empty))
        {
            var inline = new Run(run.Text);

            if (run.Bold) inline.FontWeight = FontWeights.SemiBold;
            if (run.Color is { } color) inline.Foreground = color;

            Inlines.Add(inline);
        }
    }
}

/// <summary>一段同色文本。</summary>
internal readonly record struct AnsiRun(string Text, Brush? Color, bool Bold);

/// <summary>把带 ANSI 转义序列的文本拆成可渲染片段（纯函数，便于单测）。</summary>
internal static class AnsiParser
{
    /// <summary>SGR：ESC [ 参数 m</summary>
    private static readonly Regex Sgr = new(@"\x1B\[([0-9;]*)m", RegexOptions.Compiled);

    /// <summary>其它 CSI 序列（光标移动/清屏等）：一律剥离。</summary>
    private static readonly Regex OtherCsi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private static readonly Brush Default38 = Make("#56B6C2");
    private static readonly Brush Red = Make("#E06C75");
    private static readonly Brush Green = Make("#98C379");
    private static readonly Brush Yellow = Make("#E5C07B");
    private static readonly Brush Blue = Make("#61AFEF");
    private static readonly Brush Magenta = Make("#C678DD");
    private static readonly Brush White = Make("#ABB2BF");
    private static readonly Brush BrightGrey = Make("#9DA5B4");

    public static IReadOnlyList<AnsiRun> Parse(string text)
    {
        var runs = new List<AnsiRun>();
        if (string.IsNullOrEmpty(text))
            return runs;

        var color = -1;
        var bold = false;
        var position = 0;

        foreach (Match match in Sgr.Matches(text))
        {
            AddRun(runs, text.Substring(position, match.Index - position), color, bold);
            ApplyCodes(match.Groups[1].Value, ref color, ref bold);
            position = match.Index + match.Length;
        }

        AddRun(runs, text.Substring(position), color, bold);
        return runs;
    }

    private static void AddRun(List<AnsiRun> runs, string raw, int color, bool bold)
    {
        var plain = OtherCsi.Replace(raw, string.Empty);
        if (plain.Length == 0) return;

        runs.Add(new AnsiRun(plain, BrushFor(color), bold));
    }

    private static void ApplyCodes(string codes, ref int color, ref bool bold)
    {
        if (string.IsNullOrWhiteSpace(codes))
        {
            color = -1;
            bold = false;
            return;
        }

        foreach (var part in codes.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var code)) continue;

            switch (code)
            {
                case 0:
                    color = -1;
                    bold = false;
                    break;
                case 1:
                    bold = true;
                    break;
                case 22:
                    bold = false;
                    break;
                case 39:
                    color = -1;
                    break;
                default:
                    // 30-37 前景色、90-97 亮色前景
                    if (code is >= 30 and <= 37 or >= 90 and <= 97) color = code;
                    break;
            }
        }
    }

    private static Brush? BrushFor(int code) => code switch
    {
        30 => Brushes.Black,
        31 => Red,
        32 => Green,
        33 => Yellow,
        34 => Blue,
        35 => Magenta,
        36 => Default38,
        37 => White,
        90 => BrightGrey,
        91 => Red,
        92 => Green,
        93 => Yellow,
        94 => Blue,
        95 => Magenta,
        96 => Default38,
        97 => White,
        _ => null,
    };

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
