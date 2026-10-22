using System;
using UMango.Core.Utils;

namespace UMango.Core.App.Essentials;

/// <summary>
/// 一次「发现新版本」的结果。
/// </summary>
/// <param name="Version">远端最新版本号</param>
/// <param name="LocalVersion">本地当前版本号</param>
/// <param name="DownloadUrl">安装包下载地址</param>
/// <param name="FileSize">远端声明的体积（字节），未知为 null</param>
/// <param name="Notes">发行说明（可能为 null）</param>
/// <param name="IsPreRelease">是否为预发布版本</param>
public sealed record UpdateInfo(
    SemVer Version,
    SemVer LocalVersion,
    string DownloadUrl,
    long? FileSize,
    string? Notes,
    bool IsPreRelease)
{
    /// <summary>是否确实比本地新（忽略大小写与预发布标记，仅比较三段号）。</summary>
    public bool IsNewer => Version > LocalVersion;
}

/// <summary>
/// 版本检查的输入。
/// </summary>
public sealed record UpdateCheckRequest(
    string LocalVersion,
    string FeedUrl,
    bool IncludePreRelease = false);

/// <summary>
/// 版本检查失败的原因分类，供上层给出可操作提示。
/// </summary>
public enum UpdateCheckError
{
    /// <summary>网络不可达 / 超时 / DNS 失败</summary>
    Network,
    /// <summary>远端返回的不是预期结构（JSON 结构变了、返回了 HTML 错误页）</summary>
    BadPayload,
    /// <summary>远端版本号无法解析</summary>
    BadVersion,
    /// <summary>远端没有可用的安装包资源</summary>
    NoAsset
}

/// <summary>
/// 版本检查失败详情。
/// </summary>
public sealed record UpdateCheckFailure(UpdateCheckError Error, string Message, Exception? Inner = null);
