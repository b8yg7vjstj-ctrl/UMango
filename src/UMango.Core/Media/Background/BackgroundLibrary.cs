// SPDX-License-Identifier: Apache-2.0
//
// 动态背景：背景内容库（图片 / 视频）。
//
// 采用自 PCL-CE 的设计语义：
//   原始文件：Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml.cs
//             （`BackgroundRefresh`，第 310-420 行）
//   （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
//   不复制其代码；本文件为独立重写。
//
// PCL 的原版行为：
//   · 背景目录 = <exe>/PCL/Pictures/，目录不存在就建；
//   · 扫描时排除 .ini 与 .db，其余文件一律收下（能不能显示是显示阶段的事）；
//   · 随机挑一个文件当背景；
//   · **先试图片**：图片加载成功就用图片；失败则退而用视频（MediaElement）播放；
//   · 视频出错时按 0xC00D109B 判定"不是 H.264 格式"，给专门的提示。
//
// UMango 的差异（写进注释与对照表）：
//   1. 目录可配置（PCL 写死 <exe>/PCL/Pictures）；
//   2. 分类显式（图片 / 视频各一份清单），而不是"全收下、显示时再试"；
//      显式分类让"随机抽一张图"与"随机抽一段视频"这两件事可分别表述；
//   3. 格式判定交给显示层（解码器才知道支持什么），Core 只做**扩展名层面的**粗判。

namespace UMango.Core.Media.Background;

/// <summary>
/// 背景内容的类别。
/// </summary>
public enum BackgroundKind
{
    /// <summary>图片（静态背景）。</summary>
    Image,

    /// <summary>视频（动态背景）。</summary>
    Video,
}

/// <summary>
/// 一张背景图片。
/// </summary>
/// <param name="Path">文件完整路径。</param>
/// <param name="FileName">文件名（含扩展名）。</param>
/// <param name="Title">标题（无扩展名的文件名）。</param>
/// <param name="Extension">扩展名（含点，小写）。</param>
public sealed record BackgroundImage(string Path, string FileName, string Title, string Extension);

/// <summary>
/// 一段背景视频。
/// </summary>
/// <param name="Path">文件完整路径。</param>
/// <param name="FileName">文件名（含扩展名）。</param>
/// <param name="Title">标题（无扩展名的文件名）。</param>
/// <param name="Extension">扩展名（含点，小写）。</param>
public sealed record BackgroundVideo(string Path, string FileName, string Title, string Extension);

/// <summary>
/// 背景内容库：扫描目录并分类出图片与视频。
///
/// 这一层是**纯逻辑**，不碰 WPF 也不碰解码器 → 可在 macOS 上直接单测。
/// 真正的解码与显示在 App 层（<c>UMango.App.Media.Background.BackgroundPresenter</c>）。
/// </summary>
public sealed class BackgroundLibrary
{
    /// <summary>
    /// 扫描时排除的扩展名（小写，含点）。
    ///
    /// 取自 PCL 原版：它排除 <c>.ini</c> 与 <c>.db</c>，其余一律收下。
    /// </summary>
    public static IReadOnlySet<string> ExcludedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ini", ".db" };

    /// <summary>按扩展名判为图片的集合（小写，含点）。</summary>
    public static IReadOnlySet<string> ImageExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
        };

    /// <summary>按扩展名判为视频的集合（小写，含点）。</summary>
    public static IReadOnlySet<string> VideoExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv",
        };

    private readonly List<BackgroundImage> _images = [];
    private readonly List<BackgroundVideo> _videos = [];
    private readonly List<string> _candidates = [];

    /// <summary>全部图片（按路径排序）。</summary>
    public IReadOnlyList<BackgroundImage> Images => _images;

    /// <summary>全部视频（按路径排序）。</summary>
    public IReadOnlyList<BackgroundVideo> Videos => _videos;

    /// <summary>
    /// 全部候选文件（按路径排序）—— 包含扩展名未知、既不算图也不算视频的文件。
    ///
    /// 对应 PCL 的做法：它把目录里除 <c>.ini</c>/<c>.db</c> 外的**所有**文件收进
    /// 一个列表，然后**先试当图片加载，失败再退当视频播**。
    /// 也就是说 PCL 不按扩展名分类，而是**按加载结果**决定类型。
    ///
    /// UMango 两种都留着：<see cref="Images"/> / <see cref="Videos"/> 供按类型选用，
    /// <see cref="Candidates"/> 供「按 PCL 语义」逐个试。
    /// </summary>
    public IReadOnlyList<string> Candidates => _candidates;

    /// <summary>库是否为空（既没图也没视频）。</summary>
    public bool IsEmpty => _images.Count == 0 && _videos.Count == 0;

    /// <summary>
    /// 重新扫描目录，刷新图片与视频清单。
    /// </summary>
    /// <param name="directory">背景目录。</param>
    /// <returns>扫到的背景内容总数（图片 + 视频）。</returns>
    public int Reload(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _images.Clear();
        _videos.Clear();
        _candidates.Clear();

        foreach (var file in Scan(directory))
        {
            _candidates.Add(file);

            var extension = Path.GetExtension(file).ToLowerInvariant();

            if (ImageExtensions.Contains(extension))
            {
                _images.Add(new BackgroundImage(
                    file,
                    Path.GetFileName(file),
                    Path.GetFileNameWithoutExtension(file),
                    extension));
            }
            else if (VideoExtensions.Contains(extension))
            {
                _videos.Add(new BackgroundVideo(
                    file,
                    Path.GetFileName(file),
                    Path.GetFileNameWithoutExtension(file),
                    extension));
            }
        }

        return _images.Count + _videos.Count;
    }

    /// <summary>
    /// 从候选里挑一个。
    /// </summary>
    /// <param name="random">
    /// 随机源。留 null 则用 <see cref="Random.Shared"/>。
    /// 留这个口子是为了让「随机挑一张背景」可被测试断言。
    /// </param>
    /// <returns>挑到的文件路径；没有候选时返回 null。</returns>
    public string? PickCandidate(Random? random = null)
    {
        if (_candidates.Count == 0) return null;

        var rng = random ?? Random.Shared;
        return _candidates[rng.Next(_candidates.Count)];
    }

    /// <summary>
    /// 扫描目录里的候选文件（排除 <see cref="ExcludedExtensions"/>，按路径排序）。
    /// </summary>
    public static IReadOnlyList<string> Scan(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory)) return [];

        var result = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (ExcludedExtensions.Contains(Path.GetExtension(path))) continue;
            result.Add(path);
        }

        // 按路径排序 → 「非随机」模式下顺序稳定（否则每次扫描顺序都不同）。
        result.Sort(static (a, b) => string.Compare(a, b, StringComparison.Ordinal));
        return result;
    }
}
