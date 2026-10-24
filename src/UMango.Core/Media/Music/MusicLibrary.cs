// SPDX-License-Identifier: Apache-2.0
//
// 背景音乐：曲目模型与音乐库（目录扫描 + 过滤 + 随机）。
//
// 采用自 PCL-CE（PCL-Community/PCL-CE）的设计语义：
//   原始文件：Plain Craft Launcher 2/Modules/ModMusic.cs（MusicListInit 部分）
//   （**受限许可目录**，按《PCL 分发有限许可》处理）—— 本文件**只借鉴其设计语义**，
//   不复制其代码；本文件为独立重写。
//
// PCL 的原版行为：
//   · 音乐目录 = <exe>/PCL/Musics，目录不存在就建；
//   · 扫描时**排除**若干非音频扩展（.ini/.jpg/.txt/.cfg/.lrc/.db/.png），
//     其余一律收下（能不能播是播放阶段的事，不是扫描阶段的事）；
//   · 按「随机播放」设置决定待播列表的顺序（打乱 vs 原序）；
//   · 队列取空后重新初始化，并尽量不把刚播完的那首排在第一位
//     （`preventFirst` 机制：把 preventFirst 挪到队尾）。

namespace UMango.Core.Media.Music;

/// <summary>
/// 一首曲目。
/// </summary>
/// <param name="Path">文件完整路径。</param>
/// <param name="FileName">文件名（含扩展名）。</param>
/// <param name="Title">曲名（无扩展名的文件名）。</param>
/// <param name="Extension">扩展名（含点，小写）。</param>
public sealed record MusicTrack(string Path, string FileName, string Title, string Extension)
{
    /// <summary>是否是本实现能直接解码播放的格式。</summary>
    public bool IsSupportedFormat => SupportedExtensions.Contains(Extension);

    /// <summary>本实现支持直接播放的扩展名（PCL 同样只认这三种）。</summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac" };
}

/// <summary>
/// 音乐库：扫描目录并维护待播队列。
///
/// 这一层是**纯逻辑**，不碰音频设备 —— 因此可以在 macOS 上直接跑单测。
/// 真正的解码与播放在 App 层（<c>UMango.App.Media.Music.NaudioMusicPlayer</c>）。
/// </summary>
public sealed class MusicLibrary
{
    /// <summary>
    /// 扫描目录时排除的非音频扩展名（小写，含点）。
    ///
    /// 取自 PCL-CE 原版（`ModMusic.MusicListInit`）：它排除这一串，
    /// 其余扩展名一律收下 —— 能不能播是播放阶段的事。
    /// </summary>
    public static IReadOnlySet<string> ExcludedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ini", ".jpg", ".txt", ".cfg", ".lrc", ".db", ".png",
        };

    private readonly List<MusicTrack> _all = [];
    private readonly List<MusicTrack> _waiting = [];
    private readonly Random _random;

    /// <summary>创建音乐库。</summary>
    /// <param name="random">
    /// 随机源（用于「随机播放」）。留空则用默认随机源。
    /// 测试可注入固定种子以获得可复现的顺序。
    /// </param>
    public MusicLibrary(Random? random = null) => _random = random ?? new Random();

    /// <summary>全部曲目（扫描结果）。</summary>
    public IReadOnlyList<MusicTrack> All => _all;

    /// <summary>待播队列。</summary>
    public IReadOnlyList<MusicTrack> Waiting => _waiting;

    /// <summary>音乐库是否为空。</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>
    /// 重新扫描目录，填充曲目列表并重建待播队列。
    /// </summary>
    /// <param name="directory">音乐目录。</param>
    /// <param name="shuffle">是否随机打乱待播顺序。</param>
    /// <param name="preventFirst">
    /// 尽量不让这首成为队列第一项（PCL 的同名机制：播放完一首后
    /// 重新初始化队列时把它挪到队尾，避免立刻重复播放）。
    /// </param>
    /// <returns>扫描到的曲目数。</returns>
    public int Reload(string directory, bool shuffle, string? preventFirst = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _all.Clear();
        _all.AddRange(Scan(directory));
        RebuildQueue(shuffle, preventFirst);
        return _all.Count;
    }

    /// <summary>
    /// 扫描目录里的曲目（不改动队列）。
    /// </summary>
    public static IReadOnlyList<MusicTrack> Scan(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory)) return [];

        var result = new List<MusicTrack>();

        // 递归扫描：PCL 用 ModBase.EnumerateFiles，也是递归的。
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (ExcludedExtensions.Contains(extension)) continue;

            result.Add(new MusicTrack(
                path,
                Path.GetFileName(path),
                Path.GetFileNameWithoutExtension(path),
                extension.ToLowerInvariant()));
        }

        // 按路径排序，保证「非随机」模式下顺序稳定（否则每次扫描顺序都不同）。
        result.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        return result;
    }

    /// <summary>
    /// 重建待播队列。
    /// </summary>
    /// <param name="shuffle">是否随机打乱。</param>
    /// <param name="preventFirst">尽量不让这首排在第一位（找不到就忽略）。</param>
    public void RebuildQueue(bool shuffle, string? preventFirst = null)
    {
        _waiting.Clear();
        _waiting.AddRange(shuffle ? Shuffle(_all) : _all);

        if (preventFirst is null || _waiting.Count == 0) return;

        // PCL 只在队首恰好是 preventFirst 时才挪走（它只比较 [0]）。
        // 这里保持同样语义：只处理队首那一个。
        var head = _waiting[0];
        if (!string.Equals(head.Path, preventFirst, StringComparison.OrdinalIgnoreCase)) return;

        _waiting.RemoveAt(0);
        _waiting.Add(head);
    }

    /// <summary>
    /// 查看队列中的下一首**但不取走**它。
    /// </summary>
    /// <param name="shuffle">队列空时重建是否随机。</param>
    /// <returns>下一首曲目；没有可播的返回 null。</returns>
    public MusicTrack? PeekNext(bool shuffle)
    {
        if (_waiting.Count == 0) RebuildQueue(shuffle);
        return _waiting.Count == 0 ? null : _waiting[0];
    }

    /// <summary>
    /// 取出下一首待播曲目并把它移出队列。
    /// </summary>
    /// <param name="shuffle">队列取空后重建时是否随机。</param>
    /// <param name="preventFirst">重建队列时尽量不让这首排在第一位。</param>
    /// <returns>下一首曲目；没有可播的返回 null。</returns>
    public MusicTrack? DequeueNext(bool shuffle, string? preventFirst = null)
    {
        if (_waiting.Count == 0)
        {
            RebuildQueue(shuffle, preventFirst);
            if (_waiting.Count == 0) return null;
        }

        var next = _waiting[0];
        _waiting.RemoveAt(0);

        // 队列空了但曲子还没播完 → 重新初始化（PCL 的 `DequeueNextMusicAddress`）。
        // 注意 PCL 传的是**刚播完的那首**作为 preventFirst。
        if (_waiting.Count == 0) RebuildQueue(shuffle, next.Path);

        return next;
    }

    /// <summary>把某首曲目从列表中移除（播放失败时调用，PCL 也是这么做的）。</summary>
    public void Remove(MusicTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _all.RemoveAll(t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
        _waiting.RemoveAll(t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fisher–Yates 洗牌（用注入的随机源）。</summary>
    private List<MusicTrack> Shuffle(IEnumerable<MusicTrack> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
