// SPDX-License-Identifier: Apache-2.0
//
// 设计来源：PCL-CE `Plain Craft Launcher 2/Pages/PageTools/PageToolsTest.xaml.cs`
// 的「自定义下载」工具（受限许可目录 —— 只借鉴设计语义，独立重写，不复制代码）。
//
// PCL 的做法：填 URL + 文件名 + 目录 → 校验 URL（HttpValidator）→ 拼成
// `DownloadFile` → 交给 `LoaderDownload` / `LoaderDownloadUnc` → 走加载器管线。
//
// UMango 的差异：
//   1. PCL 把「选择保存位置」的对话框写在 Core 层的静态方法里（依赖 WPF）；
//      本文件**只做校验与规范化**，选择位置由 App 层负责。
//   2. PCL 支持 UNC 路径（`\\server\share`）走另一个加载器；UMango 走统一引擎，
//      UNC 路径由引擎层自行处理（`System.Uri` 能解析 UNC）。
//   3. PCL 的文件名可以随意填（用户能填出带路径分隔符的名字 → 写到别处去）；
//      本版**规范化**文件名：剥掉路径分隔符、拒绝 `.`/`..`、拒绝 Windows 保留名。
//   4. PCL 用 `HttpValidator`（正则）判 URL；本版用 `Uri.TryCreate` + 协议白名单。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UMango.Core.Tools;

/// <summary>自定义下载请求的校验结果。</summary>
/// <param name="IsValid">是否通过全部校验。</param>
/// <param name="Url">规范化后的 URL（仅在通过时非空）。</param>
/// <param name="FileName">规范化后的文件名（仅在通过时非空）。</param>
/// <param name="Directory">规范化后的目标目录（仅在通过时非空）。</param>
/// <param name="ErrorKey">失败原因对应的语言键（仅在失败时非空）。</param>
public sealed record CustomDownloadValidation(
    bool IsValid,
    string? Url,
    string? FileName,
    string? Directory,
    string? ErrorKey);

/// <summary>自定义下载的校验与规范化。</summary>
public static class CustomDownloadRequest
{
    /// <summary>允许的 URL 协议。</summary>
    public static readonly IReadOnlyList<string> AllowedSchemes = ["http", "https", "ftp", "file"];

    /// <summary>Windows 保留设备名（不区分大小写）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 校验并规范化一次自定义下载请求。
    /// </summary>
    /// <param name="url">用户填的 URL。</param>
    /// <param name="fileName">用户填的文件名（可空 → 从 URL 推断）。</param>
    /// <param name="directory">用户填的目标目录（可空 → 用默认目录）。</param>
    /// <param name="defaultDirectory">目标目录为空时的兜底目录。</param>
    public static CustomDownloadValidation Validate(
        string? url,
        string? fileName,
        string? directory,
        string? defaultDirectory = null)
    {
        // 1) URL
        var trimmedUrl = url?.Trim() ?? "";
        if (trimmedUrl.Length == 0)
            return Invalid("Validation.Empty");

        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri))
            return Invalid("Validation.InvalidUrl");

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            return Invalid("Validation.InvalidUrl");

        // 2) 文件名
        var name = NormalizeFileName(fileName);
        if (name is null)
            return Invalid("Validation.InvalidChar");

        if (name.Length == 0)
        {
            name = DeriveFileName(uri);
            if (string.IsNullOrWhiteSpace(name))
                return Invalid("Validation.Empty");
        }

        // 3) 目录
        var dir = directory?.Trim();
        if (string.IsNullOrWhiteSpace(dir))
            dir = defaultDirectory?.Trim();

        if (string.IsNullOrWhiteSpace(dir))
            return Invalid("Validation.Empty");

        try
        {
            dir = Path.GetFullPath(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid("Validation.BadPathSegment");
        }

        return new CustomDownloadValidation(true, uri.AbsoluteUri, name, dir, null);
    }

    private static CustomDownloadValidation Invalid(string errorKey)
        => new(false, null, null, null, errorKey);

    /// <summary>
    /// 规范化文件名：剥掉路径分隔符与目录部分、拒绝 <c>.</c>/<c>..</c>、
    /// 拒绝 Windows 保留设备名、拒绝控制字符。
    /// </summary>
    /// <returns>规范化后的文件名；无法规范化时返回 null。</returns>
    public static string? NormalizeFileName(string? fileName)
    {
        if (fileName is null) return null;

        var name = fileName.Trim();
        if (name.Length == 0) return "";

        // 只取最后一段：用户可能填了 "C:\path\to\file.zip" 或 "dir/file.zip"
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        if (name.Length == 0) return null;
        if (name is "." or "..") return null;

        foreach (var c in name)
        {
            if (char.IsControl(c)) return null;
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*') return null;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem)) return null;

        return name;
    }

    /// <summary>从 URL 推断文件名。取路径最后一段；没有就用主机名。</summary>
    public static string DeriveFileName(Uri uri)
    {
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : "";
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(segment))
        {
            var decoded = Uri.UnescapeDataString(segment);
            var normalized = NormalizeFileName(decoded);
            if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        }

        return NormalizeFileName(uri.Host) ?? "download";
    }
}
