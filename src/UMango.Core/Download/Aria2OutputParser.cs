using System.Globalization;
using System.Text.RegularExpressions;

namespace UMango.Core.Download;

/// <summary>
/// aria2 输出解析。
///
/// **格式来自真机实测**（本机 aria2c 1.37.0），不是照文档猜的。
/// 实测到的进度读数行长这样：
/// <code>
/// [#a8c198 1.0MiB/5.4MiB(18%) CN:1 DL:519KiB ETA:8s]
///    GID    已下/总计   百分比   连接数  速度    剩余时间
/// </code>
/// 完成时输出 <c>Download complete: &lt;path&gt;</c>，结尾有汇总表：
/// <code>
/// Download Results:
/// gid   |stat|avg speed  |path/URI
/// ======+====+===========+=======================================================
/// b2f0cf|OK  |   7.4MiB/s|/tmp/aria_big.bin
/// </code>
///
/// ⚠️ 两个真机坑（已在引擎参数里规避，这里仍要容错）：
///   1. **aria2 的消息是本地化的**（中文环境下输出「下载完成：」），
///      所以引擎强制 LC_ALL=C，但解析器同时接受中英文两种措辞；
///   2. 输出带 ANSI 颜色码，引擎用 --enable-color=false 关掉，解析器仍会先剥码。
/// </summary>
public static partial class Aria2OutputParser
{
    // 进度读数行：[#gid 1.0MiB/5.4MiB(18%) CN:1 DL:519KiB ETA:8s]
    [GeneratedRegex(@"\[#(?<gid>\w+)\s+(?<done>[\d.]+)(?<du>[KMGTP]?i?B)/(?<total>[\d.]+)(?<tu>[KMGTP]?i?B)\((?<pct>\d+)%\)(?<rest>[^\]]*)\]",
        RegexOptions.Compiled)]
    private static partial Regex ReadoutPattern();

    // 剥 ANSI 颜色码（--enable-color=false 之外的兜底）
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled)]
    private static partial Regex AnsiPattern();

    // 汇总表数据行：b2f0cf|OK  |   7.4MiB/s|/tmp/aria_big.bin
    [GeneratedRegex(@"^(?<gid>[0-9a-fA-F]{6,16})\|(?<stat>[A-Z]+)\s*\|(?<speed>[^|]*)\|(?<path>.+)$",
        RegexOptions.Compiled)]
    private static partial Regex SummaryRowPattern();

    /// <summary>去掉 ANSI 颜色码。</summary>
    public static string StripAnsi(string line) => AnsiPattern().Replace(line, "");

    /// <summary>
    /// 尝试从一行输出解析进度。解析不到返回 null（**不编造进度**）。
    /// </summary>
    public static DownloadProgress? TryParseProgress(string line)
    {
        var clean = StripAnsi(line);
        var m = ReadoutPattern().Match(clean);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups["pct"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
            return null;

        long? done = ToBytes(m.Groups["done"].Value, m.Groups["du"].Value);
        long? total = ToBytes(m.Groups["total"].Value, m.Groups["tu"].Value);

        string? speed = null, eta = null;
        var rest = m.Groups["rest"].Value;
        var dl = Regex.Match(rest, @"DL:(\S+)");
        if (dl.Success) speed = dl.Groups[1].Value;
        var etaM = Regex.Match(rest, @"ETA:(\S+)");
        if (etaM.Success) eta = etaM.Groups[1].Value;

        return new DownloadProgress(
            Fraction: Math.Clamp(pct / 100.0, 0, 1),
            BytesDone: done,
            BytesTotal: total,
            SpeedText: speed,
            EtaText: eta,
            Gid: m.Groups["gid"].Value);
    }

    /// <summary>是否是「下载完成」行（兼容中英文；aria2 的消息会随 locale 变）。</summary>
    public static bool IsCompletion(string line, out string? path)
    {
        var clean = StripAnsi(line);
        foreach (var marker in new[] { "Download complete:", "下载完成：" })
        {
            var idx = clean.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                path = clean[(idx + marker.Length)..].Trim();
                return true;
            }
        }
        path = null;
        return false;
    }

    /// <summary>是否是错误行。</summary>
    public static bool IsError(string line) =>
        StripAnsi(line).Contains("[ERROR]", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析汇总表数据行；不是数据行则返回 null。</summary>
    public static (string Gid, string Stat, string Path)? TryParseSummaryRow(string line)
    {
        var m = SummaryRowPattern().Match(StripAnsi(line).Trim());
        if (!m.Success) return null;
        return (m.Groups["gid"].Value, m.Groups["stat"].Value, m.Groups["path"].Value.Trim());
    }

    /// <summary>把 "1.0" + "MiB" 这样的组合换算成字节数。</summary>
    public static long? ToBytes(string number, string unit)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;

        var u = unit.ToUpperInvariant().Replace("I", "");   // MiB -> MB, KB -> KB
        var factor = u switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            "PB" => 1024L * 1024 * 1024 * 1024 * 1024,
            _ => 0L
        };
        return factor == 0 ? null : (long)(n * factor);
    }
}
