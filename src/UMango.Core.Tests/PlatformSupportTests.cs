namespace UMango.Core.Tests;

/// <summary>
/// 需求 1：**平台门控**。
///
/// 界面要能表达"该包源在当前系统上不可用"，靠的就是 PackageSourceKindExtensions.SupportedPlatforms /
/// Supports —— 适配器实例上的 SupportedPlatforms 只是转述同一份声明，两边必须一致。
/// 这里同时断言枚举声明与真实适配器实例，避免"枚举改了、适配器没跟上"这种偏差。
/// </summary>
public sealed class PlatformSupportTests
{
    private static readonly PlatformKind[] ThreePlatforms =
        [PlatformKind.Windows, PlatformKind.MacOs, PlatformKind.Linux];

    /// <summary>
    /// 平台清单比较。用 SequenceEqual 而不是 Assert.Equal(集合, 集合)：
    /// 后者会命中 Assert.Equal&lt;T&gt;(T, T) 的重载（T = IReadOnlyList&lt;...&gt;），
    /// 语义容易变成"比引用"，这里显式按元素顺序比较，断言意图不含糊。
    /// </summary>
    private static void AssertPlatforms(IReadOnlyList<PlatformKind> actual, string because, params PlatformKind[] expected)
        => Assert.True(
            expected.SequenceEqual(actual),
            $"{because}：期望 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", actual)}]");

    [Theory]
    [InlineData(PackageSourceKind.Winget)]
    [InlineData(PackageSourceKind.Scoop)]
    public void Windows专属包源_只支持Windows(PackageSourceKind kind)
    {
        AssertPlatforms(kind.SupportedPlatforms(), $"{kind.DisplayName()} 的 SupportedPlatforms", PlatformKind.Windows);
        Assert.True(kind.Supports(PlatformKind.Windows));
        Assert.False(kind.Supports(PlatformKind.MacOs), $"{kind.DisplayName()} 不应声明支持 macOS");
        Assert.False(kind.Supports(PlatformKind.Linux), $"{kind.DisplayName()} 不应声明支持 Linux");
    }

    [Theory]
    [InlineData(PackageSourceKind.Npm)]
    [InlineData(PackageSourceKind.Pnpm)]
    [InlineData(PackageSourceKind.Git)]
    public void 跨平台包源_三个平台都支持(PackageSourceKind kind)
    {
        AssertPlatforms(kind.SupportedPlatforms(), $"{kind.DisplayName()} 的 SupportedPlatforms", ThreePlatforms);
        foreach (var platform in ThreePlatforms)
            Assert.True(kind.Supports(platform), $"{kind.DisplayName()} 应支持 {platform.DisplayName()}");
    }

    [Fact]
    public void Homebrew_不含Windows()
    {
        var platforms = PackageSourceKind.Homebrew.SupportedPlatforms();
        Assert.DoesNotContain(PlatformKind.Windows, platforms);
        Assert.False(PackageSourceKind.Homebrew.Supports(PlatformKind.Windows));
        Assert.True(PackageSourceKind.Homebrew.Supports(PlatformKind.MacOs));
        Assert.True(PackageSourceKind.Homebrew.Supports(PlatformKind.Linux));
    }

    [Fact]
    public void 每种包源都必须给出非空且自洽的平台清单()
    {
        foreach (var kind in Enum.GetValues<PackageSourceKind>())
        {
            Assert.NotEmpty(kind.SupportedPlatforms());

            // Supports 与 SupportedPlatforms 必须同源，否则 UI 两处判断会打架
            foreach (var platform in Enum.GetValues<PlatformKind>())
                Assert.Equal(kind.SupportedPlatforms().Contains(platform), kind.Supports(platform));
        }
    }

    [Fact]
    public void Winget与Scoop适配器_实例上的平台清单只含Windows()
    {
        IPackageManager[] windowsOnlyAdapters = [new WingetPackageManager(), new ScoopPackageManager()];
        foreach (var adapter in windowsOnlyAdapters)
        {
            AssertPlatforms(adapter.SupportedPlatforms, $"{adapter.GetType().Name} 的 SupportedPlatforms", PlatformKind.Windows);
            // 适配器实例必须转述枚举里的同一份声明
            Assert.True(
                adapter.Kind.SupportedPlatforms().SequenceEqual(adapter.SupportedPlatforms),
                $"{adapter.GetType().Name} 的平台清单与 PackageSourceKind.{adapter.Kind} 的声明不一致");
        }
    }

    [Fact]
    public void Homebrew适配器_不含Windows且含macOS()
    {
        var brew = new HomebrewPackageManager();
        Assert.DoesNotContain(PlatformKind.Windows, brew.SupportedPlatforms);
        Assert.Contains(PlatformKind.MacOs, brew.SupportedPlatforms);
        Assert.True(
            brew.Kind.SupportedPlatforms().SequenceEqual(brew.SupportedPlatforms),
            "HomebrewPackageManager 的平台清单与枚举声明不一致");
    }

    [Fact]
    public void Npm与Pnpm适配器_三平台都支持()
    {
        IPackageManager[] crossPlatformAdapters = [new NpmPackageManager(), new PnpmPackageManager()];
        foreach (var adapter in crossPlatformAdapters)
            AssertPlatforms(adapter.SupportedPlatforms, $"{adapter.GetType().Name} 的 SupportedPlatforms", ThreePlatforms);
    }
}
