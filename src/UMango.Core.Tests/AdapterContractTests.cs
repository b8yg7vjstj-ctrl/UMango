namespace UMango.Core.Tests;

/// <summary>
/// 适配器**契约巡检**：反射扫描 UMango.Core 里 UMango.Core.PackageManagers.Adapters 命名空间下
/// 所有已实现的适配器，逐个验证不变量。好处是并行开发期间新加的适配器（Git 等）
/// 会自动纳入检查，不需要改这个文件。
///
/// 巡检的不变量：
///   ① 必须是 public sealed class 且可无参构造（注册表按实例装配，界面直接 new）；
///   ② PackageSourceKind 不能重复（注册表 Find 只返回第一个，重复是缺陷）；
///   ③ 平台清单必须与枚举声明一致、CliName / InstallHint 非空；
///   ④ 分类文案：Title = 包管理器名 = Kind.DisplayName()，Subtitle = InstallDirectory；
///   ⑤ 本机不支持的包源：GetAvailabilityAsync 必须返回 UnsupportedPlatform 且无 CLI 痕迹。
/// </summary>
public sealed class AdapterContractTests
{
    private const string AdapterNamespace = "UMango.Core.PackageManagers.Adapters";

    /// <summary>
    /// 扫描适配器类型。GetTypes 在本程序集含无法加载的类型时只失败一部分，
    /// 这里退回成功加载的那部分，避免整条契约巡检因为一个无关类型而挂掉。
    /// </summary>
    private static IReadOnlyList<Type> DiscoverAdapterTypes()
    {
        Type?[] types;
        try
        {
            types = typeof(PackageManagerRegistry).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        return types
            .Where(t => t is { IsClass: true, IsAbstract: false, Namespace: AdapterNamespace })
            .Where(t => typeof(IPackageManager).IsAssignableFrom(t))
            .Select(t => t!)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<IPackageManager> InstantiateAdapters()
    {
        var list = new List<IPackageManager>();
        foreach (var type in DiscoverAdapterTypes())
        {
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            if (Activator.CreateInstance(type) is IPackageManager adapter) list.Add(adapter);
        }
        return list;
    }

    [Fact]
    public void 适配器都是public_sealed且能无参构造()
    {
        var types = DiscoverAdapterTypes();
        Assert.NotEmpty(types);   // 至少要能发现参考实现 NpmPackageManager

        foreach (var type in types)
        {
            Assert.True(type.IsPublic, $"{type.Name} 必须是 public");
            Assert.True(type.IsSealed, $"{type.Name} 必须是 sealed");
            Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        }

        Assert.Equal(types.Count, InstantiateAdapters().Count);
    }

    [Fact]
    public void 每个包源只能有一个适配器()
    {
        var adapters = InstantiateAdapters();
        Assert.NotEmpty(adapters);

        var duplicates = adapters
            .GroupBy(a => a.Kind)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}（{string.Join(", ", g.Select(a => a.GetType().Name))}）")
            .ToList();

        Assert.True(duplicates.Count == 0, "同一包源出现多个适配器：" + string.Join("；", duplicates));
    }

    [Fact]
    public void 每个适配器_平台清单Cli名与安装指引都齐备()
    {
        var adapters = InstantiateAdapters();
        Assert.NotEmpty(adapters);

        foreach (var adapter in adapters)
        {
            var name = adapter.GetType().Name;

            // 平台清单必须与枚举声明一致（UI 用枚举判断，适配器实例也要转述同一份）
            Assert.True(
                adapter.Kind.SupportedPlatforms().SequenceEqual(adapter.SupportedPlatforms),
                $"{name} 的 SupportedPlatforms 与 PackageSourceKind.{adapter.Kind} 的声明不一致");

            Assert.False(string.IsNullOrWhiteSpace(adapter.CliName), $"{name} 缺少 CLI 名");
            Assert.False(string.IsNullOrWhiteSpace(adapter.InstallHint), $"{name} 缺少未安装时的安装指引");
            Assert.False(string.IsNullOrWhiteSpace(adapter.InstallDirectory), $"{name} 缺少（默认）安装目录");
            Assert.Contains(adapter.Kind.Id(), name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task 每个适配器_分类标题是包管理器名_子标题是安装目录()
    {
        var adapters = InstantiateAdapters();
        Assert.NotEmpty(adapters);

        var registry = new PackageManagerRegistry(adapters);
        var categories = await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None);

        Assert.Equal(adapters.Count, categories.Count);
        foreach (var adapter in adapters)
        {
            var category = categories.Single(c => c.Kind == adapter.Kind);
            Assert.Equal(adapter.Kind.DisplayName(), category.Title);   // 标题 = 包管理器名
            Assert.Equal(adapter.DisplayName, category.Title);
            Assert.Equal(adapter.InstallDirectory, category.Subtitle);  // 子标题 = 安装目录
            Assert.False(string.IsNullOrWhiteSpace(category.Subtitle));
            Assert.Equal(adapter.Capabilities, category.Capabilities);
        }
    }

    [Fact]
    public async Task 本机不支持的适配器_可用性为UnsupportedPlatform且无CLI痕迹()
    {
        var adapters = InstantiateAdapters();
        Assert.NotEmpty(adapters);
        var registry = new PackageManagerRegistry(adapters);

        var unsupported = adapters.Where(a => !a.Kind.Supports(SystemInfo.Platform)).ToList();
        var supported = adapters.Where(a => a.Kind.Supports(SystemInfo.Platform)).ToList();

        foreach (var adapter in unsupported)
        {
            var availability = await registry.GetAvailabilityAsync(adapter.Kind, refresh: true, CancellationToken.None);
            Assert.Equal(AvailabilityState.UnsupportedPlatform, availability.State);
            Assert.False(availability.CliFound);
            Assert.Null(availability.CliPath);
            Assert.Null(availability.CliVersion);   // 没有任何"执行过 CLI"的痕迹
        }

        if (SystemInfo.Platform != PlatformKind.Windows)
        {
            // 非 Windows 主机上，winget / scoop 必须落在"不支持"集合里（界面上应被隐藏）
            var windowsOnly = adapters
                .Where(a => a.Kind is PackageSourceKind.Winget or PackageSourceKind.Scoop)
                .ToList();

            Assert.True(windowsOnly.Count > 0, "未发现 winget/scoop 适配器：平台门控缺少真实适配器覆盖");
            Assert.All(windowsOnly, adapter => Assert.Contains(adapter, unsupported));
        }
        else
        {
            // Windows 主机上反过来：winget / scoop 必须落在"支持"集合里
            var windowsOnly = adapters
                .Where(a => a.Kind is PackageSourceKind.Winget or PackageSourceKind.Scoop)
                .ToList();

            Assert.All(windowsOnly, adapter => Assert.Contains(adapter, supported));
        }
    }

    [Fact]
    public async Task 每个适配器_安装规划都按下载方式拆成多个子项()
    {
        var adapters = InstantiateAdapters();
        Assert.NotEmpty(adapters);

        foreach (var adapter in adapters)
        {
            if (!adapter.Capabilities.Install) continue;   // 不支持安装的包源不作要求

            var name = adapter.GetType().Name;

            // git 这类适配器会把 Clone 与 Checkout 拆成两步，都不该是 Unknown；
            // 断言用 TryLoadManifest 之类的探测命令时也只做本地规划，不执行安装。
            var steps = await adapter.PlanInstallAsync("umango-probe-package", null, CancellationToken.None);

            Assert.True(steps.Count > 1, $"{name} 的安装规划只有 {steps.Count} 个步骤，无法按下载方式列子项进度");
            Assert.All(steps, s => Assert.NotEqual(DownloadMethod.Unknown, s.Method));
            Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        }
    }
}
