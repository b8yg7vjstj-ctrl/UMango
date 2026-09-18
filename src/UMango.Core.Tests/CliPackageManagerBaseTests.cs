namespace UMango.Core.Tests;

/// <summary>
/// 基类 <see cref="CliPackageManagerBase"/> 共性能力的直接验证。
///
/// 这些逻辑（容错 JSON 解析、"包不存在"归一化、"不支持"响应、安装目录回退）
/// 是六个适配器共用的，一旦回归会同时影响所有包源，所以即使它们是 protected 也值得单独测：
/// 测试替身 <see cref="FakeCliPackageManager"/> 继承基类后把 protected 成员包成 public 静态方法，
/// 无需改动 UMango.Core 就能断言。
/// </summary>
public sealed class CliPackageManagerBaseTests
{
    // ---------- 容错 JSON 解析（CLI 常把警告混进 stdout） ----------

    [Fact]
    public void 容错解析_能跳过夹杂在JSON前后的警告文本()
    {
        // 真机形态：npm warn 一类警告走在 JSON 前面，直接 Deserialize 必然失败
        const string raw = "npm warn deprecated foo@1.0.0\n{\"name\":\"express\",\"version\":\"4.19.2\"}\n";

        using var doc = FakeCliPackageManager.ParseJsonProbe<JsonDocument>(raw, "测试");

        Assert.NotNull(doc);
        Assert.Equal("express", doc!.RootElement.GetProperty("name").GetString());
        Assert.Equal("4.19.2", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void 容错解析_能截取出夹杂在文本中间的JSON数组()
    {
        const string raw = "Updating index...\n[{\"name\":\"a\"},{\"name\":\"b\"}]\nDone in 1s\n";

        using var doc = FakeCliPackageManager.ParseJsonProbe<JsonDocument>(raw, "测试");

        Assert.NotNull(doc);
        Assert.Equal(JsonValueKind.Array, doc!.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void 容错解析_空输出返回null而不是抛异常()
    {
        // CLI 输出为空通常表示"没有内容"，不应被当成错误（例如 scoop search 无匹配）
        Assert.Null(FakeCliPackageManager.ParseJsonProbe<JsonDocument>("", "测试"));
        Assert.Null(FakeCliPackageManager.ParseJsonProbe<JsonDocument>("   \n\t ", "测试"));
    }

    [Fact]
    public void 容错解析_彻底不是JSON时抛parse_error()
    {
        var ex = Assert.Throws<ProcessException>(() =>
        {
            FakeCliPackageManager.ParseJsonProbe<JsonDocument>("not-json", "测试");
        });

        Assert.Equal("parse_error", ex.Code);
        Assert.Contains("not-json", ex.StdErr);   // 原始片段要带上，便于用户回报真实格式
    }

    [Fact]
    public void 容错解析_JSON不完整时抛parse_error()
    {
        var ex = Assert.Throws<ProcessException>(() =>
        {
            FakeCliPackageManager.ParseJsonProbe<JsonDocument>("{\"name\":", "测试");
        });

        Assert.Equal("parse_error", ex.Code);
    }

    // ---------- "包不存在"归一化 ----------

    [Fact]
    public void 包不存在的输出_被归一化识别()
    {
        Assert.True(FakeCliPackageManager.LooksLikeNotFoundProbe("npm ERR! code E404", ""));
        Assert.True(FakeCliPackageManager.LooksLikeNotFoundProbe("", "Package x is not in this registry."));
        Assert.True(FakeCliPackageManager.LooksLikeNotFoundProbe("", "Error: no package found"));
        Assert.True(FakeCliPackageManager.LooksLikeNotFoundProbe("not installed", ""));
    }

    [Fact]
    public void 网络类错误_不应被误判成包不存在()
    {
        Assert.False(FakeCliPackageManager.LooksLikeNotFoundProbe("ETIMEDOUT", "network error"));
        Assert.False(FakeCliPackageManager.LooksLikeNotFoundProbe("", "ERR! code EACCES"));
        Assert.False(FakeCliPackageManager.LooksLikeNotFoundProbe("", ""));
    }

    // ---------- 截断与"不支持" ----------

    [Fact]
    public void 截断_超长文本加省略号_不超长则原样返回()
    {
        Assert.Equal("abcdef", FakeCliPackageManager.TruncateProbe("abcdef", 10));
        Assert.Equal("abc…", FakeCliPackageManager.TruncateProbe("abcdef", 3));
        Assert.Equal("", FakeCliPackageManager.TruncateProbe("", 5));
    }

    [Fact]
    public async Task 未覆盖的能力_默认返回unsupported_feature()
    {
        // 测试替身没有覆盖任何能力方法 → 搜索与版本列表都应报"不支持"，
        // 而不是静默返回空结果（否则界面会把"不支持"显示成"没找到"）
        var fake = new FakeCliPackageManager(PackageSourceKind.Winget, @"C:\fake\WinGet");

        var searchEx = await Assert.ThrowsAsync<ProcessException>(
            () => fake.SearchAsync("express", 10, CancellationToken.None));
        Assert.Equal("unsupported_feature", searchEx.Code);

        var versionsEx = await Assert.ThrowsAsync<ProcessException>(
            () => fake.GetVersionsAsync("express", CancellationToken.None));
        Assert.Equal("unsupported_feature", versionsEx.Code);

        // 异常文案里要带上包管理器名，便于用户理解是哪个包源不支持
        Assert.Contains("WinGet", searchEx.Message);
    }

    [Fact]
    public async Task 基类默认_依赖查询返回空列表而不是抛异常()
    {
        // winget 不暴露依赖关系：按设计要求"不覆盖该方法"，基类默认返回空列表
        var fake = new FakeCliPackageManager(PackageSourceKind.Winget);
        var deps = await fake.GetDependenciesAsync("express", null, CancellationToken.None);
        Assert.Empty(deps);
    }

    // ---------- 安装目录回退与显示名 ----------

    [Fact]
    public void 未探测时_子标题回退到平台默认安装目录()
    {
        var fake = new FakeCliPackageManager(PackageSourceKind.Npm, "/fake/node/lib/node_modules");
        Assert.Equal("/fake/node/lib/node_modules", fake.InstallDirectory);
        Assert.Equal("npm", fake.DisplayName);   // DisplayName 默认取枚举里的包管理器名
        Assert.Equal("npm", fake.CliName);
    }

    [Fact]
    public void CLI名与包源的对应关系固定()
    {
        // 探测、调用、失败归一化都依赖这个映射，写死断言防止被顺手改掉
        Assert.Equal("npm", new FakeCliPackageManager(PackageSourceKind.Npm).CliName);
        Assert.Equal("pnpm", new FakeCliPackageManager(PackageSourceKind.Pnpm).CliName);
        Assert.Equal("git", new FakeCliPackageManager(PackageSourceKind.Git).CliName);
        Assert.Equal("scoop", new FakeCliPackageManager(PackageSourceKind.Scoop).CliName);
        Assert.Equal("winget", new FakeCliPackageManager(PackageSourceKind.Winget).CliName);
        Assert.Equal("brew", new FakeCliPackageManager(PackageSourceKind.Homebrew).CliName);
    }

    // ---------- 探测：CLI 不在 PATH ----------

    [Fact]
    public async Task CLI不在PATH时_探测结果为NotInstalled而不是抛异常()
    {
        // 取当前平台**确实支持**的包源，再把 PATH 换成空目录：
        // 这样"找不到 CLI"是确定性的，不受开发机是否装了 brew/winget 影响
        var kind = SystemInfo.Platform == PlatformKind.Windows
            ? PackageSourceKind.Winget
            : PackageSourceKind.Homebrew;
        var fake = new FakeCliPackageManager(kind, "/fake/install-dir");

        using var emptyPath = EmptyPathScope.Create();
        Assert.Null(ProcessRunner.Which(fake.CliName));   // 前提校验：PATH 为空，必然找不到

        var availability = await fake.ProbeAsync(CancellationToken.None);

        Assert.Equal(AvailabilityState.NotInstalled, availability.State);
        Assert.Equal(kind, availability.Kind);
        Assert.False(availability.CliFound);
        Assert.Null(availability.CliPath);
        Assert.False(availability.IsUsable);
        Assert.Contains(fake.CliName, availability.Detail ?? "");   // 提示里要说清是哪个 CLI 找不到
        Assert.Equal(1, fake.ProbeCallCount);
    }

    [Fact]
    public async Task 平台不支持时_探测直接返回UnsupportedPlatform()
    {
        // 非 Windows 主机上 winget 必定被门控挡掉；Windows 上则必须放行到真实探测
        var winget = new FakeCliPackageManager(PackageSourceKind.Winget, @"C:\fake\WinGet");

        var availability = await winget.ProbeAsync(CancellationToken.None);
        Assert.Equal(1, winget.ProbeCallCount);

        if (SystemInfo.Platform == PlatformKind.Windows)
            Assert.NotEqual(AvailabilityState.UnsupportedPlatform, availability.State);
        else
            Assert.Equal(AvailabilityState.UnsupportedPlatform, availability.State);
    }
}
