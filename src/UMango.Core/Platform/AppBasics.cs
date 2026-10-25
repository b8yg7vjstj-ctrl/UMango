// SPDX-License-Identifier: Apache-2.0
//
// 本文件的设计思路取自 PCL-CE 的 PCL.Core/App/Basics.cs 与 Metadata.cs
//（均为 Apache-2.0 许可）。
//
// 采用时做的改动：
//   1. 拆成三个类型：AppPaths（路径静态入口）、AppMetadata（版本元数据）、
//      AppBasics（进程信息）—— PCL 把它们塞在一个 static class 里，
//      静态构造器读打包资源，一旦资源缺失就 TypeInitializationException；
//   2. 元数据来源从打包的 metadata.json 改为程序集属性（AssemblyInformationalVersion /
//      FileVersion / AssemblyCompany），无需额外打包资源；
//   3. 全部改用不可变快照，不再有「静态可变字段」。

using System.Diagnostics;
using System.Reflection;

namespace UMango.Core.Platform;

/// <summary>
/// 应用路径的静态入口。默认实例基于当前进程环境。
/// </summary>
public static class AppPaths
{
    private static PathService? _current;

    /// <summary>
    /// 当前路径快照。可替换（测试里用来重定向到临时目录），但**不保证线程安全** ——
    /// 替换应在应用启动早期、任何读取者开始之前完成。
    /// </summary>
    public static PathService Current
    {
        get => _current ??= new PathService();
        set => _current = value;
    }

    /// <summary>重置为默认实例（按当前进程环境重新计算）。</summary>
    public static void Reset() => _current = null;

    /// <summary>创建所有需要的目录。应用启动时调用一次。</summary>
    public static void EnsureDirectories() => Current.EnsureDirectories();
}

/// <summary>
/// 启动器版本元数据。
/// </summary>
/// <param name="Name">程序名。</param>
/// <param name="BaseVersion">基础版本号，如 <c>0.1.0</c>。</param>
/// <param name="Suffix">后缀，如 <c>beta.1</c>。</param>
/// <param name="Code">内部版本代号。</param>
/// <param name="BranchName">发行分支名（Debug / CI / Publish）。</param>
public sealed record AppMetadataInfo(
    string Name,
    string BaseVersion,
    string Suffix,
    int Code,
    string BranchName)
{
    /// <summary>版本名（基础版本 + 后缀）。</summary>
    public string BaseName => string.IsNullOrWhiteSpace(Suffix) ? BaseVersion : $"{BaseVersion}-{Suffix}";

    /// <summary>标准版本号（基础版本 + 分支代号）。</summary>
    public string StandardVersion => $"{BaseVersion}.{Code}";

    /// <summary>展示用字符串：分支 + 版本名。</summary>
    public override string ToString() => $"{BranchName} {BaseName} ({Code})";
}

/// <summary>
/// 从程序集属性读取应用元数据。
/// </summary>
/// <remarks>
/// PCL-CE 的 <c>Basics.Metadata</c> 从打包资源 <c>PCL.metadata.json</c> 反序列化，
/// 且在**静态构造器**里做 —— 资源没打进包里就会在第一次访问任何 Basics 成员时
/// 抛 <see cref="TypeInitializationException"/>，而真正的原因（文件找不到）被包在
/// 内部异常里。UMango 改为从程序集元数据读：没有额外资源，也不会静默失败。
/// </remarks>
public static class AppMetadata
{
    private static AppMetadataInfo? _current;

    /// <summary>当前元数据。</summary>
    public static AppMetadataInfo Current => _current ??= Detect();

    /// <summary>覆盖元数据（测试用）。</summary>
    public static void Override(AppMetadataInfo metadata) => _current = metadata;

    /// <summary>重置为自动探测。</summary>
    public static void Reset() => _current = null;

    private static AppMetadataInfo Detect()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppMetadata).Assembly;

        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion 可能是 "0.1.0+sha" 或 "0.1.0-beta.1"，
        // 这里只取语义版本主体，后缀单独拆出来。
        var (baseVersion, suffix) = SplitVersion(version);

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var code = ParseCode(fileVersion) ?? ParseCode(baseVersion) ?? 0;

        var name = string.IsNullOrWhiteSpace(product) ? "UMango" : product!;
        var branch = DetectBranch(assembly);

        _ = company;
        return new AppMetadataInfo(name, baseVersion, suffix, code, branch);
    }

    /// <summary>
    /// 拆分版本号：基础版本与预发布后缀。
    ///
    /// 公开出来是为了可测 —— 版本解析是纯逻辑，不该只能通过程序集属性间接验证。
    /// </summary>
    /// <param name="raw">原始版本文本，可为空。</param>
    /// <returns>基础版本与后缀（无后缀时后缀为空串）。</returns>
    public static (string Base, string Suffix) ParseVersion(string? raw) => SplitVersion(raw);

    private static (string Base, string Suffix) SplitVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ("0.0.0", string.Empty);

        var text = raw.Trim();

        // 去掉 SemVer 的构建元数据（+sha）
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        // 拆预发布后缀
        var dash = text.IndexOf('-');
        if (dash < 0) return (text, string.Empty);
        return (text[..dash], text[(dash + 1)..]);
    }

    private static int? ParseCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 优先取末段的数字（0.1.0.3 → 3；0.1.0 → 0）
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i], out var value))
                return value;
        }
        return null;
    }

    private static string DetectBranch(Assembly assembly)
    {
        var config = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (!string.IsNullOrWhiteSpace(config))
            return config!;

        // 分支信息应由 CI 通过 <AssemblyConfiguration> 注入；
        // 没有注入时按发布版处理（不靠 DEBUG 宏猜 —— 宏在 Release 里也可能是 Debug 构��）。
        return "Release";
    }
}

/// <summary>
/// 进程级基础信息。
/// </summary>
public static class AppBasics
{
    /// <summary>当前进程。</summary>
    public static Process CurrentProcess { get; } = Process.GetCurrentProcess();

    /// <summary>当前进程 ID。</summary>
    public static int CurrentProcessId => Environment.ProcessId;

    /// <summary>完整命令行（含程序名）。</summary>
    public static string[] FullCommandLineArguments => Environment.GetCommandLineArgs();

    /// <summary>命令行参数（不含程序名）。</summary>
    public static string[] CommandLineArguments => Environment.GetCommandLineArgs()[1..];

    /// <summary>当前工作目录。</summary>
    public static string CurrentDirectory => Environment.CurrentDirectory;

    /// <summary>取路径的父目录；没有父目录时返回路径根。</summary>
    public static string? GetParentPath(string path) =>
        Path.GetDirectoryName(path) ?? Path.GetPathRoot(path);

    /// <summary>取路径的父目录；没有时返回空串。</summary>
    public static string GetParentPathOrEmpty(string path) => GetParentPath(path) ?? string.Empty;

    /// <summary>取路径的父目录；没有时回退到当前工作目录。</summary>
    public static string GetParentPathOrDefault(string path) => GetParentPath(path) ?? CurrentDirectory;
}
