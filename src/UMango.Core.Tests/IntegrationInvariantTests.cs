namespace UMango.Core.Tests;

/// <summary>
/// 收敛期补充的**集成不变量**测试。
///
/// 与 <see cref="AdapterContractTests"/>（反射巡检"每个适配器自身对不对"）互补：
/// 这里验证的是"把六个适配器装到一起之后"的约束 ——
///   ① <see cref="PackageManagerFactory.CreateDefault"/> 注册全部六个包源，且**不做平台过滤**；
///   ② 平台门控仍然只由 SupportedPlatforms 决定（winget / scoop 只支持 Windows）；
///   ③ 官方下载量能力的真实情况：Homebrew 有 formulae.brew.sh 接口 → true，
///      pnpm / scoop / winget 确实没有该接口 → 不得放宽为 true；
///   ④ VerifyNote 机制仍在：未真机验证的适配器必须如实标注，并一路传到界面上用的分类项。
///
/// 用 <see cref="EmptyPathScope"/> 把 PATH 换成空目录，既保证不依赖开发机装了哪些 CLI，
/// 也保证用例不会真的去执行 winget / scoop / brew。
/// </summary>
public sealed class IntegrationInvariantTests
{
    [Fact]
    public void 工厂_注册全部六个包源且不做平台过滤()
    {
        var registry = PackageManagerFactory.CreateDefault();
        var expected = Enum.GetValues<PackageSourceKind>();

        Assert.True(
            expected.SequenceEqual(registry.All.Select(m => m.Kind).OrderBy(k => k)),
            "工厂必须把六个包源全部注册："
            + string.Join("、", registry.All.Select(m => m.Kind.ToString())));

        // 关键：注册期不过滤平台。不支持的包源同样要在册，
        // 否则界面只能让它凭空消失，而无法显示"该包源在当前系统不可用"。
        Assert.All(expected, kind => Assert.NotNull(registry.Find(kind)));

        // 同一包源不能注册两个实例（注册表的 Find 只返回第一个，重复会让界面出现重复分类）
        Assert.Equal(registry.All.Count, registry.All.Select(m => m.Kind).Distinct().Count());
    }

    [Fact]
    public void 工厂_每个包源挂的是对应类型的适配器()
    {
        var registry = PackageManagerFactory.CreateDefault();

        // 防止复制粘贴错误：把某个适配器实例注册到别的 Kind 上，
        // 会同时骗过"数量"与"Kind 不重复"两项检查。
        Assert.IsType<NpmPackageManager>(registry.Find(PackageSourceKind.Npm));
        Assert.IsType<PnpmPackageManager>(registry.Find(PackageSourceKind.Pnpm));
        Assert.IsType<GitPackageManager>(registry.Find(PackageSourceKind.Git));
        Assert.IsType<ScoopPackageManager>(registry.Find(PackageSourceKind.Scoop));
        Assert.IsType<WingetPackageManager>(registry.Find(PackageSourceKind.Winget));
        Assert.IsType<HomebrewPackageManager>(registry.Find(PackageSourceKind.Homebrew));
    }

    [Fact]
    public async Task 工厂产出的注册表_六个分类都在_不支持的包源被门控而不是消失()
    {
        using var emptyPath = EmptyPathScope.Create();
        var registry = PackageManagerFactory.CreateDefault();

        var categories = await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None);

        Assert.Equal(Enum.GetValues<PackageSourceKind>().Length, categories.Count);

        foreach (var category in categories)
        {
            // 标题 = 包管理器名、子标题 = 安装目录：两者都不能因为"没装 CLI"而变空
            Assert.False(string.IsNullOrWhiteSpace(category.Title), $"{category.Kind} 缺少分类标题");
            Assert.False(string.IsNullOrWhiteSpace(category.Subtitle), $"{category.Kind} 缺少安装目录（子标题）");

            bool supportedHere = category.Kind.Supports(SystemInfo.Platform);
            Assert.Equal(supportedHere, category.SupportedHere);

            if (supportedHere)
            {
                // PATH 被清空 ⇒ 必须诚实地说"未安装"，并给出安装指引（而不是异常或空文案）
                Assert.Equal(AvailabilityState.NotInstalled, category.AvailabilityState);
                Assert.False(string.IsNullOrWhiteSpace(category.InstallHint), $"{category.Kind} 未安装时缺少安装指引");
            }
            else
            {
                // 平台门控：状态与文案都要说明"当前系统不可用"
                Assert.Equal(AvailabilityState.UnsupportedPlatform, category.AvailabilityState);
                Assert.False(category.Available);
                Assert.False(string.IsNullOrWhiteSpace(category.Detail));
            }
        }
    }

    [Fact]
    public void 平台门控_winget与scoop只支持Windows()
    {
        var registry = PackageManagerFactory.CreateDefault();

        foreach (var kind in (PackageSourceKind[])[PackageSourceKind.Winget, PackageSourceKind.Scoop])
        {
            var platforms = registry.Find(kind)!.SupportedPlatforms;

            Assert.Equal([PlatformKind.Windows], platforms.ToArray());
            Assert.True(kind.Supports(PlatformKind.Windows), $"{kind.DisplayName()} 必须支持 Windows");
            Assert.False(kind.Supports(PlatformKind.MacOs), $"{kind.DisplayName()} 不得声明支持 macOS");
            Assert.False(kind.Supports(PlatformKind.Linux), $"{kind.DisplayName()} 不得声明支持 Linux");
        }
    }

    [Fact]
    public void 官方下载量能力_与各包源的真实接口一致()
    {
        var registry = PackageManagerFactory.CreateDefault();

        // Homebrew 有官方接口（formulae.brew.sh 的 install counts）→ 必须为 true
        Assert.True(
            registry.Find(PackageSourceKind.Homebrew)!.Capabilities.OfficialDownloadCounts,
            "Homebrew 有 formulae.brew.sh 官方接口，OfficialDownloadCounts 必须保持 true");

        // 这三个确实没有官方下载量接口 → 不得放宽为 true，
        // 否则界面会把 Star 数之类的兜底值当成"官方下载量"显示。
        foreach (var kind in (PackageSourceKind[])[
                     PackageSourceKind.Pnpm, PackageSourceKind.Scoop, PackageSourceKind.Winget])
        {
            Assert.False(
                registry.Find(kind)!.Capabilities.OfficialDownloadCounts,
                $"{kind.DisplayName()} 没有官方下载量接口，OfficialDownloadCounts 不得为 true");
        }
    }

    [Fact]
    public async Task 未真机验证的适配器_必须如实标注且标注传到分类项()
    {
        using var emptyPath = EmptyPathScope.Create();
        var registry = PackageManagerFactory.CreateDefault();

        // winget / scoop 是 Windows 专属，本项目在 macOS 上开发 ⇒ 本机不可能做过真机验证，
        // 因此必须带 VerificationNote（界面据此显示"未验证"提示）。无论在哪个平台跑都成立。
        foreach (var kind in (PackageSourceKind[])[PackageSourceKind.Winget, PackageSourceKind.Scoop])
        {
            var availability = await registry.GetAvailabilityAsync(kind, refresh: true, CancellationToken.None);
            Assert.False(
                string.IsNullOrWhiteSpace(availability.VerificationNote),
                $"{kind.DisplayName()} 未经真机验证，必须给出 VerificationNote");

            var category = (await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None))
                .Single(c => c.Kind == kind);
            Assert.Equal(availability.VerificationNote, category.VerificationNote);
        }
    }
}
