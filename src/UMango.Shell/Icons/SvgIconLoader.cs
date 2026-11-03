using System.Collections.Concurrent;
using System.IO;
using System.Text;
using UMango.Core.UI.Svg;

namespace UMango.Shell.Icons;

/// <summary>
/// SVG 图标加载器（含缓存），**两个界面壳共用**。
///
/// <para>
/// 图标包是纯数据（<c>.svg</c> 文本），内嵌在本工程（<c>Assets/IconPacks/&lt;包名&gt;/&lt;图标名&gt;.svg</c>），
/// 逻辑资源名为 <c>UMango.Shell.Assets.IconPacks.&lt;包名&gt;.&lt;图标名&gt;.svg</c>。
/// 为什么放在 Shell 而不是某个壳里：WPF 与 Avalonia 各存一套必然发散，
/// 而解析代码本来就在 Core（<see cref="SvgDocumentParser"/>），这里只负责取资源。
/// </para>
///
/// <para>
/// 解析结果是平台中立的 <see cref="SvgIconModel"/>；各壳自己把它画出来
/// （WPF 转 <c>Geometry</c>，Avalonia 转 <c>StreamGeometry</c>）。
/// </para>
/// </summary>
public static class SvgIconLoader
{
    /// <summary>默认图标包名。</summary>
    public const string DefaultIconPack = SvgIconKey.DefaultIconPack;

    /// <summary>资源名前缀（= 本程序集名 + 目录层级）。</summary>
    private const string ResourcePrefix = "UMango.Shell.Assets.IconPacks.";

    private static readonly ConcurrentDictionary<string, Lazy<SvgIconModel?>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按标识加载图标（如 <c>lucide/home</c>）。</summary>
    public static SvgIconModel? Load(string? icon, string? defaultPack = null)
    {
        var key = SvgIconKey.TryParse(icon, defaultPack ?? DefaultIconPack);
        if (key is null)
            return null;

        var cacheKey = key.Value.ToString();
        return Cache.GetOrAdd(cacheKey, _ => new Lazy<SvgIconModel?>(() => LoadCore(key.Value))).Value;
    }

    /// <summary>从 SVG 源码解析图标（不走资源、不入缓存）。</summary>
    public static SvgIconModel? ParseSource(string svg)
    {
        try
        {
            var root = SvgSourceParser.ParseSource(svg);
            return SvgDocumentParser.Parse(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空缓存。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 诊断用：列出本程序集里实际存在的图标资源名。
    /// 取不到图标时先用它核对资源名（大小写/目录层级都可能拼错）。
    /// </summary>
    public static IReadOnlyList<string> ListResources() =>
        typeof(SvgIconLoader).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToArray();

    private static SvgIconModel? LoadCore(SvgIconKey key)
    {
        try
        {
            var name = $"{ResourcePrefix}{key.Pack}.{key.Name}.svg";
            using var stream = typeof(SvgIconLoader).Assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return ParseSource(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }
}
