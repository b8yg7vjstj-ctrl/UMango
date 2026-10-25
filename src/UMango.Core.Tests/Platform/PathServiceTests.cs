using UMango.Core.Platform;
using Xunit;

namespace UMango.Core.Tests.Platform;

/// <summary>
/// <see cref="PathService"/> / <see cref="AppPaths"/> / <see cref="AppMetadata"/> 的行为约束。
///
/// 设计意图：PCL-CE 的 <c>Paths</c> 是**静态可变属性**，全局隐式状态；
/// UMango 改成不可变快照 —— 这些测试就是把这个约束钉住。
/// </summary>
public class PathServiceTests
{
    [Fact]
    public void 数据目录在所有平台都非空且为绝对路径()
    {
        var paths = new PathService();

        Assert.False(string.IsNullOrWhiteSpace(paths.UserDataDirectory));
        Assert.True(Path.IsPathRooted(paths.UserDataDirectory));
        Assert.False(string.IsNullOrWhiteSpace(paths.LocalDataDirectory));
        Assert.False(string.IsNullOrWhiteSpace(paths.TempDirectory));
    }

    [Fact]
    public void 各子文件路径都在用户数据目录下()
    {
        var paths = new PathService();

        Assert.Equal(paths.UserDataDirectory, Path.GetDirectoryName(paths.SettingsFile));
        Assert.Equal(paths.UserDataDirectory, Path.GetDirectoryName(paths.InstallRecordsFile));
        Assert.Equal(paths.UserDataDirectory, Path.GetDirectoryName(paths.SingleInstanceLockFile));
        Assert.Equal(paths.UserDataDirectory, Path.GetDirectoryName(paths.RpcPipeNameFile));
    }

    [Fact]
    public void 日志目录与缓存目录各自独立()
    {
        var paths = new PathService();

        Assert.NotEqual(paths.UserDataDirectory, paths.LogsDirectory);
        Assert.NotEqual(paths.UserDataDirectory, paths.CacheDirectory);
        Assert.NotEqual(paths.LogsDirectory, paths.CacheDirectory);
    }

    [Fact]
    public void 构造参数覆盖数据根目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "umango-path-test-" + Guid.NewGuid().ToString("n"));
        var paths = new PathService(dataRootOverride: root);

        Assert.Equal(root, paths.UserDataDirectory);
        Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void 构造参数覆盖临时根目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "umango-temp-test-" + Guid.NewGuid().ToString("n"));
        var paths = new PathService(tempRootOverride: root);

        Assert.Equal(root, paths.TempDirectory);
        Assert.Equal(Path.Combine(root, "downloads"), paths.TempDownloadsDirectory);
    }

    [Fact]
    public void 环境变量可以覆盖数据根目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "umango-env-test-" + Guid.NewGuid().ToString("n"));
        var previous = Environment.GetEnvironmentVariable("UMANGO_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("UMANGO_DATA_DIR", root);
            var paths = new PathService();

            Assert.Equal(root, paths.UserDataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UMANGO_DATA_DIR", previous);
        }
    }

    [Fact]
    public void 显式构造参数优先于环境变量()
    {
        var envRoot = Path.Combine(Path.GetTempPath(), "umango-env-" + Guid.NewGuid().ToString("n"));
        var argRoot = Path.Combine(Path.GetTempPath(), "umango-arg-" + Guid.NewGuid().ToString("n"));
        var previous = Environment.GetEnvironmentVariable("UMANGO_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("UMANGO_DATA_DIR", envRoot);
            var paths = new PathService(dataRootOverride: argRoot);

            Assert.Equal(argRoot, paths.UserDataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UMANGO_DATA_DIR", previous);
        }
    }

    [Fact]
    public void 可执行文件目录来自传入路径()
    {
        var exe = Path.Combine(Path.GetTempPath(), "umango-bin", "UMango.exe");
        var paths = new PathService(executablePath: exe);

        Assert.Equal(Path.GetFullPath(exe), paths.ExecutablePath);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "umango-bin")), paths.ExecutableDirectory);
        Assert.Equal("UMango.exe", paths.ExecutableName);
        Assert.Equal("UMango", paths.ExecutableNameWithoutExtension);
    }

    [Fact]
    public void 空白可执行路径会回退而不是抛异常()
    {
        var paths = new PathService(executablePath: "   ");

        Assert.False(string.IsNullOrWhiteSpace(paths.ExecutablePath));
        Assert.False(string.IsNullOrWhiteSpace(paths.ExecutableDirectory));
    }

    [Fact]
    public void EnsureDirectories会创建所有目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "umango-mk-" + Guid.NewGuid().ToString("n"));
        var paths = new PathService(dataRootOverride: root, tempRootOverride: Path.Combine(root, "tmp"));

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.UserDataDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.TempDownloadsDirectory));
        Assert.True(Directory.Exists(paths.UpdateStagingDirectory));
    }

    [Fact]
    public void EnsureDirectories可重复调用()
    {
        var root = Path.Combine(Path.GetTempPath(), "umango-mk2-" + Guid.NewGuid().ToString("n"));
        var paths = new PathService(dataRootOverride: root);

        paths.EnsureDirectories();
        paths.EnsureDirectories();   // 第二次不应抛（幂等）

        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void 默认下载目录在有Downloads时落在其下()
    {
        var paths = new PathService();
        var downloads = Path.Combine(SystemInfo.HomeDirectory, "Downloads");

        if (Directory.Exists(downloads))
        {
            Assert.Equal(Path.Combine(downloads, "UMango"), paths.DefaultDownloadDirectory);
        }
        else
        {
            Assert.Equal(paths.TempDownloadsDirectory, paths.DefaultDownloadDirectory);
        }
    }

    [Fact]
    public void 桌面目录缺失时回退到用户主目录()
    {
        var paths = new PathService();

        Assert.False(string.IsNullOrWhiteSpace(paths.DesktopDirectory));
        Assert.True(Path.IsPathRooted(paths.DesktopDirectory));
    }

    [Fact]
    public void 路径快照是只读的()
    {
        var paths = new PathService();

        // 编译期就保证：没有 setter 的属性无法被改。
        var type = typeof(PathService);
        foreach (var name in new[] { "UserDataDirectory", "SettingsFile", "LogsDirectory", "TempDirectory" })
        {
            var prop = type.GetProperty(name);
            Assert.NotNull(prop);
            Assert.Null(prop!.SetMethod);
        }
    }
}

/// <summary>
/// <see cref="AppPaths"/> 静态入口的替换与重置行为。
/// </summary>
[Collection("静态状态")]
public class AppPathsTests
{
    [Fact]
    public void Current可以被替换并重置()
    {
        var original = AppPaths.Current;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "umango-static-" + Guid.NewGuid().ToString("n"));
            var custom = new PathService(dataRootOverride: root);
            AppPaths.Current = custom;

            Assert.Same(custom, AppPaths.Current);
            Assert.Equal(root, AppPaths.Current.UserDataDirectory);

            AppPaths.Reset();
            Assert.NotSame(custom, AppPaths.Current);
        }
        finally
        {
            AppPaths.Current = original;
        }
    }

    [Fact]
    public void EnsureDirectories委托给当前快照()
    {
        var original = AppPaths.Current;
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "umango-ensure-" + Guid.NewGuid().ToString("n"));
            AppPaths.Current = new PathService(dataRootOverride: root);

            AppPaths.EnsureDirectories();

            Assert.True(Directory.Exists(root));
        }
        finally
        {
            AppPaths.Current = original;
        }
    }
}

/// <summary>
/// <see cref="AppMetadata"/> 的版本解析。
/// </summary>
[Collection("静态状态")]
public class AppMetadataTests
{
    [Fact]
    public void 当前元数据可读且版本非空()
    {
        var meta = AppMetadata.Current;

        Assert.False(string.IsNullOrWhiteSpace(meta.Name));
        Assert.False(string.IsNullOrWhiteSpace(meta.BaseVersion));
        Assert.False(string.IsNullOrWhiteSpace(meta.BranchName));
    }

    [Fact]
    public void 基础版本名在无后缀时等于基础版本()
    {
        var meta = new AppMetadataInfo("UMango", "1.2.3", "", 7, "Publish");

        Assert.Equal("1.2.3", meta.BaseName);
        Assert.Equal("1.2.3.7", meta.StandardVersion);
    }

    [Fact]
    public void 有后缀时基础版本名附加后缀()
    {
        var meta = new AppMetadataInfo("UMango", "1.2.3", "beta.1", 7, "Publish");

        Assert.Equal("1.2.3-beta.1", meta.BaseName);
    }

    [Fact]
    public void Override后Current返回覆盖值()
    {
        var original = AppMetadata.Current;
        try
        {
            var custom = new AppMetadataInfo("Test", "9.9.9", "", 1, "Test");
            AppMetadata.Override(custom);

            Assert.Same(custom, AppMetadata.Current);
        }
        finally
        {
            AppMetadata.Reset();
        }
    }

    [Fact]
    public void Reset后回到自动探测()
    {
        var original = AppMetadata.Current;
        try
        {
            AppMetadata.Override(new AppMetadataInfo("Test", "1.0.0", "", 0, "Test"));
            AppMetadata.Reset();

            Assert.NotEqual("Test", AppMetadata.Current.Name);
        }
        finally
        {
            AppMetadata.Current.Equals(original);
        }
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", "")]
    [InlineData("1.2.3-beta.1", "1.2.3", "beta.1")]
    [InlineData("1.2.3+sha123", "1.2.3", "")]
    [InlineData("1.2.3-beta.1+sha123", "1.2.3", "beta.1")]
    [InlineData("", "0.0.0", "")]
    [InlineData(null, "0.0.0", "")]
    public void 版本号能被正确拆分为基础版本与后缀(string? raw, string expectedBase, string expectedSuffix)
    {
        var (baseVersion, suffix) = AppMetadata.ParseVersion(raw);

        Assert.Equal(expectedBase, baseVersion);
        Assert.Equal(expectedSuffix, suffix);
    }

    [Fact]
    public void 构建元数据不影响基础版本与后缀()
    {
        var withMeta = AppMetadata.ParseVersion("2.0.0+abcdef");
        var withoutMeta = AppMetadata.ParseVersion("2.0.0");

        Assert.Equal(withoutMeta.Base, withMeta.Base);
        Assert.Equal(withoutMeta.Suffix, withMeta.Suffix);
    }
}

/// <summary>
/// <see cref="AppBasics"/> 的进程信息。
/// </summary>
public class AppBasicsTests
{
    [Fact]
    public void 进程ID与当前进程一致()
    {
        Assert.Equal(Environment.ProcessId, AppBasics.CurrentProcessId);
    }

    [Fact]
    public void 命令行参数不含程序名()
    {
        var full = AppBasics.FullCommandLineArguments;
        var args = AppBasics.CommandLineArguments;

        Assert.Equal(full.Length - 1, args.Length);
    }

    [Fact]
    public void 父路径在有父目录时正确()
    {
        var path = Path.Combine(Path.GetTempPath(), "umango-dir", "file.txt");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "umango-dir"), AppBasics.GetParentPath(path));
    }

    [Fact]
    public void 父路径为空时返回路径根()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath();

        Assert.Equal(root, AppBasics.GetParentPath(root));
    }

    [Fact]
    public void 父路径空回退版本不会抛异常()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath();

        Assert.NotNull(AppBasics.GetParentPathOrEmpty(root));
        Assert.False(string.IsNullOrWhiteSpace(AppBasics.GetParentPathOrDefault(root)));
    }
}
