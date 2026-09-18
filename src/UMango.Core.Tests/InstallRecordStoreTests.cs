namespace UMango.Core.Tests;

/// <summary>
/// 需求 5：**InstallRecordStore 的统计与排序**。
///
/// 注意 <see cref="InstallRecordStore.StorePath"/> 是静态只读属性、不支持注入路径，
/// 而 Add() 内部会自动落盘 —— 因此每个用例都先用 <see cref="TempHomeScope"/> 把
/// HOME/USERPROFILE/APPDATA 指向临时目录，并在调用 Add() **之前**校验重定向已生效
/// （见 AssertRedirected）：万一重定向失败就直接判失败，绝不在真实用户目录上写记录文件。
/// </summary>
public sealed class InstallRecordStoreTests
{
    private static InstallRecord Rec(PackageSourceKind source, DateTimeOffset occurredAt, bool success) => new()
    {
        Source = source,
        PackageName = "express",
        Version = "4.19.2",
        Action = "install",
        OccurredAt = occurredAt,
        Success = success,
        InstallDirectory = "/fake/node/lib/node_modules",
        DurationMs = 1234,
    };

    /// <summary>
    /// 落盘路径重定向守卫：未生效就判失败（避免覆盖真实用户的管理记录）。
    /// </summary>
    private static void AssertRedirected(TempHomeScope home)
        => Assert.True(
            home.IsEffective,
            $"无法把 InstallRecordStore.StorePath 重定向到临时目录（当前实际路径：{InstallRecordStore.StorePath}）；"
            + "为避免覆盖真实用户目录里的记录文件，本用例在此终止。");

    [Fact]
    public void Add之后_总数成功失败与按包源计数都正确()
    {
        using var home = TempHomeScope.Create();
        AssertRedirected(home);

        var store = new InstallRecordStore();
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // 故意乱序写入，用来同时验证"按时间倒序返回"
        store.Add(Rec(PackageSourceKind.Npm, t0.AddMinutes(30), true));
        store.Add(Rec(PackageSourceKind.Npm, t0, true));
        store.Add(Rec(PackageSourceKind.Pnpm, t0.AddMinutes(10), false));

        Assert.Equal(3, store.TotalCount);
        Assert.Equal(2, store.SuccessCount);
        Assert.Equal(1, store.FailureCount);

        Assert.Equal(2, store.CountOf(PackageSourceKind.Npm));
        Assert.Equal(1, store.CountOf(PackageSourceKind.Pnpm));
        Assert.Equal(0, store.CountOf(PackageSourceKind.Homebrew));
        Assert.Equal(2, store.CountBySource[PackageSourceKind.Npm]);

        var occurred = store.Records.Select(r => r.OccurredAt).ToList();
        Assert.Equal(new[] { t0.AddMinutes(30), t0.AddMinutes(10), t0 }, occurred);   // 时间倒序：最近的在前
    }

    [Fact]
    public void 记录按时间倒序返回_与写入顺序无关()
    {
        using var home = TempHomeScope.Create();
        AssertRedirected(home);

        var store = new InstallRecordStore();
        var baseTime = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        // 先写"最新"的，再写"最旧"的，最后写"中间"的
        store.Add(Rec(PackageSourceKind.Git, baseTime.AddHours(2), true));
        store.Add(Rec(PackageSourceKind.Git, baseTime, true));
        store.Add(Rec(PackageSourceKind.Git, baseTime.AddHours(1), true));

        var order = store.Records.Select(r => r.OccurredAt).ToList();
        var expected = order.OrderByDescending(t => t).ToArray();

        // 用"数组 vs 列表"两侧类型不同的写法，确保命中 xunit 的集合比较重载（逐元素比较）
        Assert.Equal(expected, order);
        Assert.Equal(baseTime.AddHours(2), order[0]);
        Assert.Equal(baseTime, order[^1]);
    }

    [Fact]
    public void 落盘与重载_记录不丢()
    {
        using var home = TempHomeScope.Create();
        AssertRedirected(home);

        var store = new InstallRecordStore();
        store.Add(Rec(PackageSourceKind.Git, DateTimeOffset.Now, true));

        Assert.True(File.Exists(InstallRecordStore.StorePath), "Add 之后应自动落盘");

        var reloaded = new InstallRecordStore();
        reloaded.Load();

        Assert.Equal(1, reloaded.TotalCount);
        Assert.Equal(1, reloaded.SuccessCount);
        Assert.Equal("express", reloaded.Records[0].PackageName);
        Assert.Equal(PackageSourceKind.Git, reloaded.Records[0].Source);
        Assert.Equal("install", reloaded.Records[0].Action);
    }

    [Fact]
    public void 记录文件落在临时目录_不碰真实用户目录()
    {
        using var home = TempHomeScope.Create();
        AssertRedirected(home);

        // 断言记录文件确实在临时目录下，且不在重定向前的真实用户目录下
        Assert.StartsWith(home.Root, InstallRecordStore.StorePath);
        if (!string.IsNullOrEmpty(home.RealHome))
            Assert.False(
                InstallRecordStore.StorePath.StartsWith(home.RealHome!, StringComparison.Ordinal),
                "记录文件不应写在真实用户目录下");
    }

    [Fact]
    public void Clear之后_统计归零()
    {
        using var home = TempHomeScope.Create();
        AssertRedirected(home);

        var store = new InstallRecordStore();
        store.Add(Rec(PackageSourceKind.Npm, DateTimeOffset.Now, false));
        Assert.Equal(1, store.TotalCount);
        Assert.Equal(1, store.FailureCount);

        store.Clear();

        Assert.Equal(0, store.TotalCount);
        Assert.Equal(0, store.SuccessCount);
        Assert.Equal(0, store.FailureCount);
        Assert.Empty(store.Records);
        Assert.Equal(0, store.CountOf(PackageSourceKind.Npm));
    }
}
