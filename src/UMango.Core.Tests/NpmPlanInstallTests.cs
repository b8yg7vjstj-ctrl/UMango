namespace UMango.Core.Tests;

/// <summary>
/// 需求 4：**安装规划必须按下载方式拆成多个 InstallStep**。
///
/// 这就是"下载进度界面按不同下载方式列明子项进度"的数据来源：
/// 界面把每个 InstallStep 渲染成一行子项，用 Method（DownloadMethod）标注该子项的下载方式，
/// 用 Weight 把子项进度加权汇总到父进度条。因此这里断言四件事：
///   ① 步骤数 &gt; 1；② 没有任何一步是 DownloadMethod.Unknown；
///   ③ 每种下载方式各占一行（不重复）；④ 权重全为正且合计为 1.0。
/// </summary>
public sealed class NpmPlanInstallTests
{
    [Fact]
    public async Task npm安装规划_按下载方式拆成多个子项()
    {
        var npm = new NpmPackageManager();

        var steps = await npm.PlanInstallAsync("express", "4.19.2", CancellationToken.None);

        Assert.True(steps.Count > 1, $"进度界面要求按下载方式列出多个子项，实际只有 {steps.Count} 个");
        Assert.All(steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title), "每个子项都要有可读标题"));
        Assert.All(steps, s => Assert.NotEqual(DownloadMethod.Unknown, s.Method));
        Assert.All(steps, s => Assert.True(s.Weight > 0, $"子项「{s.Title}」的权重必须为正，父进度条才能加权汇总"));

        // 每种下载方式各占一行：同一步骤集合里不应出现重复的下载方式
        Assert.Equal(steps.Count, steps.Select(s => s.Method).Distinct().Count());
        // 权重合计 1.0（精度 3 位，容忍 0.2+0.1+0.6+0.1 的浮点误差）
        Assert.Equal(1.0, steps.Sum(s => s.Weight), 3);

        // 至少要覆盖"依赖解析 / 下载压缩包"这两类真实下载动作
        Assert.Contains(steps, s => s.Method == DownloadMethod.DependencyResolve);
        Assert.Contains(steps, s => s.Method == DownloadMethod.RegistryTarball);
    }

    [Fact]
    public async Task npm安装规划_只做规划不做网络查询()
    {
        var npm = new NpmPackageManager();

        var withVersion = await npm.PlanInstallAsync("express", "4.19.2", CancellationToken.None);
        var withoutVersion = await npm.PlanInstallAsync("express", null, CancellationToken.None);

        // 规划只描述步骤，不解析注册表：带不带版本号都应是同一套步骤，且不需要网络
        Assert.Equal(withVersion.Count, withoutVersion.Count);
        Assert.All(withoutVersion, s => Assert.NotEqual(DownloadMethod.Unknown, s.Method));
    }

    [Fact]
    public void 每种下载方式都有界面显示名()
    {
        // 子项进度行要显示"这是什么下载方式"，任何一种方式都不能缺中文显示名
        foreach (var method in Enum.GetValues<DownloadMethod>())
            Assert.False(string.IsNullOrWhiteSpace(method.DisplayName()), $"{method} 缺少界面显示名");
    }

    [Fact]
    public void npm能力声明_与其已实现的方法一致()
    {
        var npm = new NpmPackageManager();

        Assert.Equal(PackageSourceKind.Npm, npm.Kind);
        Assert.Equal("npm", npm.CliName);
        Assert.Equal("npm", npm.DisplayName);

        // 覆盖了哪个方法，就把对应能力位置为 true（Capabilities 是 UI 决定按钮可用性的依据）
        Assert.True(npm.Capabilities.Search);
        Assert.True(npm.Capabilities.ListVersions);
        Assert.True(npm.Capabilities.ResolveDependencies);
        Assert.True(npm.Capabilities.Install);
        Assert.False(string.IsNullOrWhiteSpace(npm.InstallHint), "未安装 npm 时必须给出安装指引");
    }
}
