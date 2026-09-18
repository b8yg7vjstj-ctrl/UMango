namespace UMango.Core.Tests;

/// <summary>
/// 需求 3：**平台不支持的包源**必须返回 AvailabilityState.UnsupportedPlatform，
/// 并且**不能去执行 CLI**。
///
/// 三条独立证据：
///   ① 真实适配器（winget/scoop）在非 Windows 主机上直接返回 UnsupportedPlatform，
///      且 CliPath / CliVersion 都是 null（没有执行过 CLI 的痕迹）；
///   ② 注册表门控 —— GetAvailabilityAsync 走的是 PackageManagerRegistry 自己的门控分支；
///   ③ 测试替身计次 —— 平台不支持时 ProbeAsync **连一次都没被调用**，
///      这是"门控发生在执行 CLI 之前"最直接的证据（真实适配器无法观察到这一点）。
/// 说明：本用例的主断言针对非 Windows 主机（开发机为 macOS）；
/// 在 Windows 上会走另一分支断言"门控放行"，不会因为换机器而静默通过。
/// </summary>
public sealed class SourceAvailabilityGatingTests
{
    private static bool IsWindowsHost => SystemInfo.Platform == PlatformKind.Windows;

    [Fact]
    public async Task Winget与Scoop在非Windows主机上_返回UnsupportedPlatform且无CLI痕迹()
    {
        IPackageManager[] windowsOnly = [new WingetPackageManager(), new ScoopPackageManager()];

        foreach (var adapter in windowsOnly)
        {
            var availability = await adapter.ProbeAsync(CancellationToken.None);

            if (IsWindowsHost)
            {
                // Windows 上 winget/scoop 受支持：必须放行到真实探测（CLI 未安装时是 NotInstalled）
                Assert.NotEqual(AvailabilityState.UnsupportedPlatform, availability.State);
                Assert.Equal(adapter.Kind, availability.Kind);
                continue;
            }

            Assert.Equal(AvailabilityState.UnsupportedPlatform, availability.State);
            Assert.Equal(adapter.Kind, availability.Kind);
            Assert.False(availability.IsUsable);
            Assert.False(availability.CliFound);       // 没去 PATH 里找 CLI
            Assert.Null(availability.CliPath);         // 更没执行过 CLI
            Assert.Null(availability.CliVersion);
            Assert.Contains("仅在", availability.Detail ?? "");   // 门控文案会说明只有哪些系统可用
        }
    }

    [Fact]
    public async Task 注册表门控_Winget与Scoop在非Windows主机上被判为平台不支持()
    {
        var registry = new PackageManagerRegistry(
            [new NpmPackageManager(), new WingetPackageManager(), new ScoopPackageManager()]);

        if (IsWindowsHost)
        {
            foreach (var kind in new[] { PackageSourceKind.Winget, PackageSourceKind.Scoop })
            {
                var probe = await registry.GetAvailabilityAsync(kind, refresh: true, CancellationToken.None);
                Assert.NotEqual(AvailabilityState.UnsupportedPlatform, probe.State);
            }
            return;
        }

        var winget = await registry.GetAvailabilityAsync(PackageSourceKind.Winget, refresh: true, CancellationToken.None);
        var scoop = await registry.GetAvailabilityAsync(PackageSourceKind.Scoop, refresh: true, CancellationToken.None);

        Assert.Equal(AvailabilityState.UnsupportedPlatform, winget.State);
        Assert.Equal(AvailabilityState.UnsupportedPlatform, scoop.State);
        Assert.False(winget.CliFound);
        Assert.False(scoop.CliFound);
        Assert.Null(winget.CliPath);
        Assert.Null(scoop.CliPath);

        // 分类视图也要标成"本机不支持"，界面据此隐藏这些包源
        var categories = await registry.GetCategoriesAsync(refresh: true, ct: CancellationToken.None);
        var wingetCategory = categories.Single(c => c.Kind == PackageSourceKind.Winget);
        var scoopCategory = categories.Single(c => c.Kind == PackageSourceKind.Scoop);

        Assert.False(wingetCategory.SupportedHere);
        Assert.False(scoopCategory.SupportedHere);
        Assert.False(wingetCategory.Available);
        Assert.Equal(AvailabilityState.UnsupportedPlatform, scoopCategory.AvailabilityState);
    }

    [Fact]
    public async Task 门控在探测之前返回_测试替身的ProbeAsync一次都没被调用()
    {
        var winget = new FakeCliPackageManager(PackageSourceKind.Winget, @"C:\fake\WinGet\Packages");
        var scoop = new FakeCliPackageManager(PackageSourceKind.Scoop, @"C:\fake\scoop\apps");
        var registry = new PackageManagerRegistry([winget, scoop]);

        if (IsWindowsHost)
        {
            await registry.GetAvailabilityAsync(PackageSourceKind.Winget, refresh: true, CancellationToken.None);
            Assert.Equal(1, winget.ProbeCallCount);   // Windows 上应真的走到探测
            Assert.Equal(0, scoop.ProbeCallCount);    // 没被查询过的包源不该被探测
            return;
        }

        foreach (IPackageManager fake in new[] { winget, scoop })
        {
            var availability = await registry.GetAvailabilityAsync(fake.Kind, refresh: true, CancellationToken.None);
            Assert.Equal(AvailabilityState.UnsupportedPlatform, availability.State);
        }

        Assert.Equal(0, winget.ProbeCallCount);   // 关键：门控直接返回，未进入探测 → 没有执行任何 CLI
        Assert.Equal(0, scoop.ProbeCallCount);
    }

    [Fact]
    public async Task 未注册的包源_直接返回UnsupportedPlatform而不是抛异常()
    {
        var registry = new PackageManagerRegistry([new NpmPackageManager()]);

        var availability = await registry.GetAvailabilityAsync(
            PackageSourceKind.Homebrew, refresh: true, CancellationToken.None);

        Assert.Equal(AvailabilityState.UnsupportedPlatform, availability.State);
        Assert.Equal(PackageSourceKind.Homebrew, availability.Kind);
        Assert.False(availability.CliFound);
        Assert.Contains("未注册", availability.Detail ?? "");
    }

    [Fact]
    public async Task 同一包源重复查询_命中缓存不再重复探测()
    {
        var fake = new FakeCliPackageManager(PackageSourceKind.Npm, "/fake/npm");
        var registry = new PackageManagerRegistry([fake]);

        await registry.GetAvailabilityAsync(PackageSourceKind.Npm, refresh: false, CancellationToken.None);
        await registry.GetAvailabilityAsync(PackageSourceKind.Npm, refresh: false, CancellationToken.None);
        Assert.Equal(1, fake.ProbeCallCount);

        await registry.GetAvailabilityAsync(PackageSourceKind.Npm, refresh: true, CancellationToken.None);
        Assert.Equal(2, fake.ProbeCallCount);   // refresh 必须绕过缓存
    }
}
