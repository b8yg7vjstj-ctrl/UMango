using UMango.Core.PackageManagers;

namespace UMango.Core.Records;

/// <summary>
/// 一次安装/卸载/升级的管理记录。需求「新写系统判断与管理记录与次数记录」的落点。
/// </summary>
public sealed class InstallRecord
{
    public required PackageSourceKind Source { get; init; }
    public required string PackageName { get; init; }
    public string? Version { get; init; }
    /// <summary>操作类型：install / uninstall / upgrade</summary>
    public required string Action { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required bool Success { get; init; }
    /// <summary>该包管理器的安装目录（记录在案，便于展示与排查）</summary>
    public string? InstallDirectory { get; init; }
    public long DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? Detail { get; init; }
}
