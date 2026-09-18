namespace UMango.Core.Tests;

/// <summary>
/// 需求 2：**界面所需的两段文案**。
///
/// 需求原文是"分类标题 = 包管理器名、子标题 = 安装目录"，
/// 对应 PackageManagerRegistry.GetCategoriesAsync 产出的 PackageSourceCategory.Title / Subtitle。
/// 这里分三层断言：
///   ① 真实 npm 适配器（单个注册表）—— 最贴近实际装配方式；
///   ② 六个包源的测试替身 —— 覆盖全部分类（其余适配器的文案规则由下面第 ③ 层覆盖）；
///   ③ AdapterContractTests 里的反射巡检 —— 自动覆盖每一个已实现的真实适配器。
/// 第 ② 层刻意把 PATH 换成空目录：文案渲染绝不该依赖 CLI 是否已安装。
/// </summary>
public sealed class PackageSourceCategoryTests
{
    [Fact]
    public async Task npm注册表_标题是包管理器名_子标题是安装目录()
    {
        var npm = new NpmPackageManager();
        var registry = new PackageManagerRegistry([npm]);

        var categories = await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None);

        var category = Assert.Single(categories);
        Assert.Equal(PackageSourceKind.Npm, category.Kind);
        Assert.Equal("npm", category.Title);                 // 标题 = 包管理器名
        Assert.Equal(npm.DisplayName, category.Title);
        Assert.Equal(npm.InstallDirectory, category.Subtitle); // 子标题 = 安装目录
        Assert.False(string.IsNullOrWhiteSpace(category.Subtitle), "安装目录不能为空，否则子标题会显示空白");
        Assert.True(category.SupportedHere, "npm 在三大平台都声明支持");
        Assert.Equal(npm.Capabilities, category.Capabilities);  // record，结构相等
        Assert.Equal(npm.InstallHint, category.InstallHint);
    }

    [Fact]
    public async Task 六个包源分类_标题即包管理器名_子标题即安装目录()
    {
        // 六个分类各给一个可辨认的安装目录，便于把"子标题"和"安装目录"绑死
        (PackageSourceKind Kind, string InstallDirectory)[] specs =
        [
            (PackageSourceKind.Npm, "/fake/node/lib/node_modules"),
            (PackageSourceKind.Pnpm, "/fake/pnpm/global"),
            (PackageSourceKind.Git, "/fake/git"),
            (PackageSourceKind.Scoop, @"%USERPROFILE%\scoop\apps"),
            (PackageSourceKind.Winget, @"%LOCALAPPDATA%\Microsoft\WinGet\Packages"),
            (PackageSourceKind.Homebrew, "/fake/homebrew"),
        ];

        var fakes = specs
            .Select(s => new FakeCliPackageManager(s.Kind, s.InstallDirectory))
            .ToList<IPackageManager>();

        // 文案不能依赖 CLI：把 PATH 清空后，即使包源受支持也应显示为"未安装"，而不是抛异常
        using var emptyPath = EmptyPathScope.Create();
        var registry = new PackageManagerRegistry(fakes);

        var categories = await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None);

        Assert.Equal(specs.Length, categories.Count);
        foreach (var (kind, installDirectory) in specs)
        {
            var category = categories.Single(c => c.Kind == kind);
            Assert.Equal(kind.DisplayName(), category.Title);        // 标题 = 包管理器名
            Assert.Equal(installDirectory, category.Subtitle);       // 子标题 = 安装目录
            Assert.False(string.IsNullOrWhiteSpace(category.Subtitle));
            Assert.Equal(kind.Supports(SystemInfo.Platform), category.SupportedHere);
        }
    }

    [Fact]
    public async Task 分类标题使用约定的包管理器名()
    {
        // 写死期望值，防止 DisplayName 被顺手改成别的写法（界面文案是需求明文规定的）
        var fakes = Enum.GetValues<PackageSourceKind>()
            .Select(k => new FakeCliPackageManager(k, $"/fake/{k.Id()}"))
            .ToList<IPackageManager>();

        using var emptyPath = EmptyPathScope.Create();
        var categories = await new PackageManagerRegistry(fakes)
            .GetCategoriesAsync(refresh: true, ct: CancellationToken.None);

        Assert.Equal("npm", categories.Single(c => c.Kind == PackageSourceKind.Npm).Title);
        Assert.Equal("pnpm", categories.Single(c => c.Kind == PackageSourceKind.Pnpm).Title);
        Assert.Equal("Git", categories.Single(c => c.Kind == PackageSourceKind.Git).Title);
        Assert.Equal("Scoop", categories.Single(c => c.Kind == PackageSourceKind.Scoop).Title);
        Assert.Equal("WinGet", categories.Single(c => c.Kind == PackageSourceKind.Winget).Title);
        Assert.Equal("Homebrew", categories.Single(c => c.Kind == PackageSourceKind.Homebrew).Title);
    }

    [Fact]
    public async Task 未探测到CLI时_子标题仍回退到平台默认安装目录()
    {
        var npm = new NpmPackageManager();
        using var emptyPath = EmptyPathScope.Create();

        // 前提校验：PATH 为空 → npm 一定找不到
        Assert.Null(ProcessRunner.Which("npm"));

        var availability = await npm.ProbeAsync(CancellationToken.None);
        Assert.Equal(AvailabilityState.NotInstalled, availability.State);

        var category = Assert.Single(await new PackageManagerRegistry([npm])
            .GetCategoriesAsync(refresh: true, ct: CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(category.Subtitle));  // 界面不能出现空子标题
        Assert.Equal(npm.InstallDirectory, category.Subtitle);
        Assert.False(category.Available);
        Assert.False(string.IsNullOrWhiteSpace(category.InstallHint), "未安装时要给出安装指引");
    }
}
