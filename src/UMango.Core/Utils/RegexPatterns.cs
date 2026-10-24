// SPDX-License-Identifier: Apache-2.0
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的 PCL.Core/Utils/RegexPatterns.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 改动（相对原版）：
//   1. 只保留 UMango 用得到的正则，删去全部 Minecraft 专属模式
//      （OptiFine/Fabric/Quilt/NeoForge/Forge/Mojang 版本号等 —— UMango 不做 MC 启动器）。
//   2. 命名空间 PCL.Core.Utils → UMango.Core.Utils。
//   3. 补 SemVer 模式的显式校验副本：UMango 已有独立的 SemVer 实现（src/UMango.Core/Utils/SemVer.cs），
//      本文件里的正则仅用于「用户输入是不是一个像样的版本号」这一层校验。
//
// 保留原版的 [GeneratedRegex] 源生成写法：比 new Regex(...) 快，且避免运行时编译。
using System.Text.RegularExpressions;

namespace UMango.Core.Utils;

/// <summary>
/// 基于代码生成优化的正则表达式实例。
/// </summary>
public static partial class RegexPatterns
{
    /// <summary>换行符，包括 <c>\r\n</c> <c>\n</c> <c>\r</c> 三种。</summary>
    public static readonly Regex NewLine = _NewLine();
    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex _NewLine();

    /// <summary>
    /// 简单匹配 HTTP(S) URI，若需严格检查请使用 <see cref="FullHttpUri"/>。
    /// </summary>
    public static readonly Regex HttpUri = _HttpUri();
    private const string PatternHttpUri = @"^https?://(?:\[[^\]\s]+\]|[^/\s?#:]+)(?::\d{1,5})?(?:/[^\s?#]*)?(?:\?[^\s#]*)?(?:#\S*)?$";
    [GeneratedRegex(PatternHttpUri, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _HttpUri();

    /// <summary>
    /// 包含完整规则的 HTTP(S) URI，含有 <c>scheme</c> <c>host</c> <c>ipv6</c>
    /// <c>port</c> <c>path</c> <c>query</c> <c>fragment</c> 分组。
    /// </summary>
    public static readonly Regex FullHttpUri = _FullHttpUri();
    private const string PatternFullHttpUri =
        @"^(?<scheme>https?)://(?<host>localhost|(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})" +
        @"|\[(?<ipv6>[0-9A-Fa-f:.]+)\]|(?:(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+(?:[A-Za-z]{2,63}|xn--[" +
        @"A-Za-z0-9\-]{2,59})))(?::(?<port>6553[0-5]|655[0-2]\d|65[0-4]\d{2}|6[0-4]\d{3}|[1-5]\d{4}|[1-9]\d{0,3}))?" +
        @"(?<path>/[^\s?#]*)?(?:\?(?<query>[^\s#]*))?(?:#(?<fragment>[^\s]*))?$";
    [GeneratedRegex(PatternFullHttpUri, RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex _FullHttpUri();

    /// <summary>Windows UNC 路径：<c>\\server\share\path</c>。</summary>
    public static readonly Regex UncPath = _UncPath();
    [GeneratedRegex("""^\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+(\\[^\\/:*?"<>|]+)*\\?$""")]
    private static partial Regex _UncPath();

    /// <summary>NTFS 8.3 短文件名格式（如 <c>PROGRA~1</c>）。</summary>
    public static readonly Regex Ntfs83FileName = _Ntfs83FileName();
    [GeneratedRegex(@".{2,}~\d")]
    private static partial Regex _Ntfs83FileName();

    /// <summary>
    /// Semantic Versioning (SemVer) 规范的版本号，包含可选的 v 前缀。
    /// </summary>
    public static readonly Regex SemVer = _SemVer();
    private const string PatternSemVer =
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
        @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$";
    [GeneratedRegex(PatternSemVer, RegexOptions.ExplicitCapture)]
    private static partial Regex _SemVer();

    /// <summary>使用 IsMatch 检查是否存在中文字符。</summary>
    public static readonly Regex HasChineseChar = _HasChineseChar();
    [GeneratedRegex("[\u4e00-\u9fbb]")]
    private static partial Regex _HasChineseChar();
}
