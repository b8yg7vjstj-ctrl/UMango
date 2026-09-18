using System.Globalization;
using System.Text.RegularExpressions;

namespace UMango.Core.Install;

/// <summary>
/// 从 CLI 输出里解析进度。
///
/// **现实前提（必须诚实对待）**：包管理器不暴露分步控制接口 ——
/// `npm install` 内部把「解析依赖 / 下载 / 链接」一次做完，外部无法逐步驱动。
/// 因此所谓「按下载方式列明的子项进度」，其数据只能来自两处：
///   1. 包管理器**自己输出的阶段与百分比**（本类负责解析）；
///   2. 我们能真正分步执行的步骤（如 git clone、aria2 的每个文件）。
/// 解析不出来时不编造进度，而是把该子项标为**不确定态**。
/// </summary>
public static partial class InstallProgressParser
{
    // 形如 "45%", "45.5 %", "Downloading... 45%"
    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentPattern();

    // 形如 "[####      ]" 或 "####----" 的进度条
    [GeneratedRegex(@"\[([#=\-\.\s>]{4,})\]", RegexOptions.Compiled)]
    private static partial Regex BarPattern();

    /// <summary>尝试解析出 0.0~1.0 的进度；解析不到返回 null。</summary>
    public static double? ParsePercent(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var m = PercentPattern().Match(line);
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) &&
            pct >= 0 && pct <= 100)
        {
            return pct / 100.0;
        }

        var b = BarPattern().Match(line);
        if (b.Success)
        {
            var bar = b.Groups[1].Value;
            var filled = bar.Count(c => c is '#' or '=' or '>');
            if (bar.Length >= 4 && filled > 0) return Math.Min(1.0, (double)filled / bar.Length);
        }
        return null;
    }

    /// <summary>
    /// 判断这一行属于哪个阶段。
    /// 各包管理器的措辞不同，这里做跨管理器的关键词归并；命中不了就返回 null（归入"其他"）。
    /// </summary>
    public static string? ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var l = line.ToLowerInvariant();

        // 依赖解析
        if (ContainsAny(l, "resolv", "idealTree", "building dependency", "depends", "peer dep", "satisf"))
            return StepNames.ResolveDependencies;

        // 元数据 / 清单
        if (ContainsAny(l, "metadata", "manifest", "fetching", "lookup", "getting info", "updating source", "正在获取", "正在更新"))
            return StepNames.FetchManifest;

        // 下载
        if (ContainsAny(l, "download", "http fetch", "tarball", "bottle", "extract", "unpack", "下载", "正在下载"))
            return StepNames.Download;

        // 编译（Homebrew 源码包）
        if (ContainsAny(l, "compil", "make", "gcc", "clang", "cmake", "configure", "编译"))
            return StepNames.Build;

        // 链接与写入
        if (ContainsAny(l, "link", "added ", "installed", "pouring", "creating shim", "writing", "已安装", "链接"))
            return StepNames.LinkWrite;

        return null;
    }

    /// <summary>
    /// 关键词匹配。
    ///
    /// 这里**强制把关键词也小写化**：调用方传入的 haystack 已经 lowercase，
    /// 但关键词里很容易写成驼峰（例如 idealTree）而永远匹配不上 ——
    /// 这个 bug 被单元测试抓到过，所以在比较函数里从根上消除这类错误，
    /// 而不是靠每个关键词都记得写小写。
    /// </summary>
    private static bool ContainsAny(string lowerHaystack, params string[] needles) =>
        needles.Any(n => lowerHaystack.Contains(n.ToLowerInvariant(), StringComparison.Ordinal));
}
