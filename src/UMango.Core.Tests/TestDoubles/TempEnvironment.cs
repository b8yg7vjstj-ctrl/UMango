using UMango.Core.Platform;
using UMango.Core.Records;

namespace UMango.Core.Tests.TestDoubles;

/// <summary>
/// 把「用户配置目录」临时改到进程私有临时目录的作用域。
///
/// 为什么需要它：<see cref="InstallRecordStore.DefaultStorePath"/> 是**静态只读**属性，
/// 路径由 SystemInfo（Windows 取 %APPDATA%，macOS/Linux 取 $HOME）算出，无法注入；
/// 而 Add() 内部会自动落盘 —— 若不重定向，测试里的临时记录会覆盖用户真实的管理记录文件。
/// 因此这里同时改 HOME / USERPROFILE / APPDATA 三个环境变量（覆盖三大平台），
/// 用例开头还会用 <see cref="IsEffective"/> 校验重定向确实生效，未生效就直接判失败，
/// 绝不在真实目录上执行 Add()。
/// </summary>
public sealed class TempHomeScope : IDisposable
{
    private readonly string? _originalHome;
    private readonly string? _originalUserProfile;
    private readonly string? _originalAppData;

    private TempHomeScope(
        string root,
        string? realHome,
        string? originalHome,
        string? originalUserProfile,
        string? originalAppData)
    {
        Root = root;
        RealHome = realHome;
        _originalHome = originalHome;
        _originalUserProfile = originalUserProfile;
        _originalAppData = originalAppData;
    }

    /// <summary>临时"用户目录"根路径</summary>
    public string Root { get; }

    /// <summary>重定向前看到的真实用户目录（用于额外断言"没有写到那里"）</summary>
    public string? RealHome { get; }

    /// <summary>重定向是否真的生效（记录文件确实落在临时目录下）</summary>
    public bool IsEffective => InstallRecordStore.DefaultStorePath.StartsWith(Root, StringComparison.Ordinal);

    public static TempHomeScope Create()
    {
        var realHome = SystemInfo.HomeDirectory;
        var root = Path.Combine(Path.GetTempPath(), "umango-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var scope = new TempHomeScope(
            root,
            realHome,
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetEnvironmentVariable("USERPROFILE"),
            Environment.GetEnvironmentVariable("APPDATA"));

        Environment.SetEnvironmentVariable("HOME", root);
        Environment.SetEnvironmentVariable("USERPROFILE", root);
        Environment.SetEnvironmentVariable("APPDATA", root);

        // 关键：<see cref="AppPaths.Current"/> 是**缓存的不可变快照**，
        // 它在第一次被访问时就把「用户数据目录」算好了。
        // 改了 HOME 之后必须让它重新计算，否则重定向不生效，
        // 用例会以为自己在临时目录里、实际却在写真实用户目录。
        AppPaths.Reset();
        return scope;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);
        Environment.SetEnvironmentVariable("APPDATA", _originalAppData);

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}

/// <summary>
/// 把 PATH 指向一个空目录的作用域。
///
/// 用途：<see cref="ProcessRunner.Which"/> 完全依赖进程的 PATH，
/// 换掉 PATH 就能**确定性地**制造"CLI 未安装"的场景（不受开发机是否装了 npm/brew 影响）。
/// </summary>
public sealed class EmptyPathScope : IDisposable
{
    private readonly string? _originalPath;
    private readonly string _emptyDirectory;

    private EmptyPathScope(string emptyDirectory, string? originalPath)
    {
        _emptyDirectory = emptyDirectory;
        _originalPath = originalPath;
    }

    public static EmptyPathScope Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-emptypath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scope = new EmptyPathScope(dir, Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("PATH", dir);
        return scope;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            if (Directory.Exists(_emptyDirectory)) Directory.Delete(_emptyDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理失败
        }
    }
}
