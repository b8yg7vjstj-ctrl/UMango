namespace UMango.Core.Download;

/// <summary>
/// 镜像地址构造：从一个"规范地址"派生出各镜像的地址。
///
/// PCL-CE 只有一个 <c>FallbackUrl</c>，由调用方**手写**完整地址。
/// UMango 改成"给出规范地址 + 镜像模板"就能派生出镜像列表 —— 因为
/// 包管理器的镜像几乎都是**同一路径、不同域名**（npm 的 registry、
/// GitHub 的 raw 内容、pypi 的 files 域名……），手写每一条既啰嗦又易错。
///
/// **安全约束**：模板必须以 <c>{url}</c> 占位原地址；没有占位符的模板会被拒绝
/// （否则会下载到与请求无关的地址）。
/// </summary>
public static class MirrorUrls
{
    /// <summary>模板里的占位符。</summary>
    public const string Placeholder = "{url}";

    /// <summary>
    /// 由规范地址与一组模板派生镜像地址。
    /// </summary>
    /// <param name="canonicalUrl">规范地址。</param>
    /// <param name="templates">镜像模板，每个都要含 <see cref="Placeholder"/>。</param>
    /// <param name="names">每个模板对应的显示名（可空，默认用域名）。</param>
    public static IReadOnlyList<DownloadSource> Build(
        string canonicalUrl,
        IEnumerable<string> templates,
        IEnumerable<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var result = new List<DownloadSource>();
        if (string.IsNullOrWhiteSpace(canonicalUrl)) return result;

        result.Add(DownloadSource.Primary(canonicalUrl, "主源"));

        var nameList = names?.ToList() ?? [];
        var index = 0;
        foreach (var template in templates)
        {
            if (string.IsNullOrWhiteSpace(template)) continue;
            if (!template.Contains(Placeholder, StringComparison.Ordinal)) continue; // 拒绝无占位符的模板

            var url = template.Replace(Placeholder, canonicalUrl, StringComparison.Ordinal);
            if (string.Equals(url, canonicalUrl, StringComparison.Ordinal)) continue; // 与主源相同就不重复

            var name = index < nameList.Count && !string.IsNullOrWhiteSpace(nameList[index])
                ? nameList[index]
                : HostOf(url) ?? $"镜像 {index + 1}";

            result.Add(DownloadSource.Mirror(url, name));
            index++;
        }

        return result;
    }

    /// <summary>从 URL 里取出主机名（用于给镜像起名）。</summary>
    public static string? HostOf(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
