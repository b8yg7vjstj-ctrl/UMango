// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路取自 PCL-CE 的
// PCL.Core/App/Paths.cs、Basics.cs、Metadata.cs（均为 Apache-2.0 许可）。
//
// 采用时做的改动：
//   1. 命名空间 PCL.Core.App → UMango.Core.Platform；
//   2. 把 PCL 的「静态可变属性」改成不可变快照对象（PathService），
//      静态入口 AppPaths 只是默认实例的薄封装；
//   3. PCL 的 Paths 用 PCLCE/PCL 目录名，这里用 UMango；
//   4. PCL 用 DEBUG 下的环境变量覆盖路径，这里改成任何构建都能用的
//      UMANGO_DATA_DIR / UMANGO_TEMP_DIR（跨平台、便于测试与便携运行）；
//   5. PCL 的 Basics.ExecutableDirectory 依赖 Environment.ProcessPath
//      （单文件发布时可能为 null），这里做回退；
//   6. PCL 的 Metadata 从打包的 metadata.json 读，UMango 改从程序集元数据读
//      （见 AppMetadata），版本号来源是 src/Directory.Build.props 的 <Version>。

using System.Reflection;

namespace UMango.Core.Platform;

/// <summary>
/// 应用级路径快照。
///
/// **为什么是对象而不是一堆静态属性**：PCL-CE 的 <c>Paths</c> 是静态可变属性
/// （<c>Paths.Data = "…"</c>），任何代码都能在运行期改掉它，于是"当前用哪个目录"
/// 变成全局隐式状态 —— 多线程下会读到半初始化的值，也无法在测试里重定向。
/// UMango 改成**不可变快照**：构造时把所有路径算完，之后只读。
/// 需要换根目录时构造新实例（测试里就是这么做的），而不是改全局。
/// </summary>
public sealed class PathService
{
    /// <summary>应用数据目录名（Windows 与 Unix 都用小写形式之外的驼峰保持一致）。</summary>
    public const string AppFolderName = "UMango";

    /// <summary>构造路径快照。</summary>
    /// <param name="executablePath">可执行文件绝对路径（可为空 → 回退到当前程序集位置）。</param>
    /// <param name="dataRootOverride">用户数据根目录覆盖（可为空）。</param>
    /// <param name="tempRootOverride">临时目录覆盖（可为空）。</param>
    public PathService(string? executablePath = null, string? dataRootOverride = null, string? tempRootOverride = null)
    {
        ExecutablePath = ResolveExecutablePath(executablePath);
        ExecutableDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;
        ExecutableName = Path.GetFileName(ExecutablePath);
        ExecutableNameWithoutExtension = Path.GetFileNameWithoutExtension(ExecutablePath);

        // 用户数据根：Windows 用 %AppData%（漫游），其余用 ~/.config。
        // 覆盖优先：构造参数 > 环境变量 > 平台默认值。
        var dataRoot = FirstNonEmpty(
            dataRootOverride,
            Env("UMANGO_DATA_DIR"),
            SystemInfo.IsWindows
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppFolderName)
                : Path.Combine(SystemInfo.HomeDirectory, ".config", "umango"));

        var localDataRoot = FirstNonEmpty(
            dataRootOverride,
            Env("UMANGO_LOCAL_DATA_DIR"),
            SystemInfo.IsWindows
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName)
                : dataRoot);

        var tempRoot = FirstNonEmpty(
            tempRootOverride,
            Env("UMANGO_TEMP_DIR"),
            Path.Combine(Path.GetTempPath(), AppFolderName));

        UserDataDirectory = dataRoot;
        LocalDataDirectory = localDataRoot;
        TempDirectory = tempRoot;

        // 各类子目录：集中在这里，避免各处各拼一次（这是 PCL 散落、UMango 收拢的收益点）。
        SettingsFile = Path.Combine(dataRoot, "settings.json");
        InstallRecordsFile = Path.Combine(dataRoot, "install-records.json");
        LogsDirectory = Path.Combine(dataRoot, "logs");
        CacheDirectory = Path.Combine(localDataRoot, "cache");
        TempDownloadsDirectory = Path.Combine(tempRoot, "downloads");
        UpdateStagingDirectory = Path.Combine(tempRoot, "update");
        PipeStateDirectory = dataRoot;
        SingleInstanceLockFile = Path.Combine(dataRoot, "instance.lock");
        RpcPipeNameFile = Path.Combine(dataRoot, "rpc-pipe.txt");
    }

    /// <summary>可执行文件绝对路径。</summary>
    public string ExecutablePath { get; }

    /// <summary>可执行文件所在目录。</summary>
    public string ExecutableDirectory { get; }

    /// <summary>可执行文件名（含扩展名）。</summary>
    public string ExecutableName { get; }

    /// <summary>可执行文件名（不含扩展名）。</summary>
    public string ExecutableNameWithoutExtension { get; }

    /// <summary>用户数据根目录（设置、日志、记录）。</summary>
    public string UserDataDirectory { get; }

    /// <summary>本地数据根目录（缓存等可丢的数据；Windows 上与漫游目录分开）。</summary>
    public string LocalDataDirectory { get; }

    /// <summary>临时目录根。</summary>
    public string TempDirectory { get; }

    /// <summary>设置文件路径。</summary>
    public string SettingsFile { get; }

    /// <summary>安装记录文件路径。</summary>
    public string InstallRecordsFile { get; }

    /// <summary>日志目录。</summary>
    public string LogsDirectory { get; }

    /// <summary>缓存目录。</summary>
    public string CacheDirectory { get; }

    /// <summary>下载临时目录。</summary>
    public string TempDownloadsDirectory { get; }

    /// <summary>更新暂存目录。</summary>
    public string UpdateStagingDirectory { get; }

    /// <summary>管道状态文件所在目录（放用户数据目录，保证同一用户同一份）。</summary>
    public string PipeStateDirectory { get; }

    /// <summary>单实例锁文件路径。</summary>
    public string SingleInstanceLockFile { get; }

    /// <summary>RPC 管道名记录文件路径。</summary>
    public string RpcPipeNameFile { get; }

    /// <summary>默认下载输出目录（用户 Downloads 下建 UMango 子目录；没有 Downloads 时回退到临时目录）。</summary>
    public string DefaultDownloadDirectory
    {
        get
        {
            var downloads = Path.Combine(SystemInfo.HomeDirectory, "Downloads");
            return Directory.Exists(downloads)
                ? Path.Combine(downloads, AppFolderName)
                : TempDownloadsDirectory;
        }
    }

    /// <summary>桌面目录（诊断包导出等用；没有桌面目录时回退到用户主目录）。</summary>
    public string DesktopDirectory
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrWhiteSpace(desktop) ? SystemInfo.HomeDirectory : desktop;
        }
    }

    /// <summary>
    /// 创建所有需要存在的目录。
    ///
    /// PCL-CE 在 <c>Paths</c> 的静态构造器里就建目录 —— 副作用藏在类型初始化里，
    /// 一旦初始化失败，整个类型的任何访问都会抛 <see cref="TypeInitializationException"/>
    /// （异常被包了一层，原始原因难查）。UMango 改成**显式调用**。
    /// </summary>
    public void EnsureDirectories()
    {
        foreach (var dir in new[]
                 {
                     UserDataDirectory,
                     LocalDataDirectory,
                     TempDirectory,
                     LogsDirectory,
                     CacheDirectory,
                     TempDownloadsDirectory,
                     UpdateStagingDirectory,
                 })
        {
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
    }

    private static string ResolveExecutablePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        // Environment.ProcessPath 在单文件发布时可能为 null（某些宿主场景）。
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return processPath;

        // 退回入口程序集位置：非单文件发布时它就是 exe 所在目录。
        var location = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(location))
            return Path.GetFullPath(location);

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c;
        return AppContext.BaseDirectory;
    }
}
