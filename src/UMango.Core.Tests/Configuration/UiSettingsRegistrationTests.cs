using UMango.Core.Configuration;
using Xunit;

namespace UMango.Core.Tests.Configuration;

/// <summary>
/// 设置项注册完整性 —— 覆盖「设置项被读取但从未注册」这类静默故障。
///
/// <para>
/// 背景：<c>SettingsService.Get</c> / <c>Set</c> 都会先取描述符，
/// 而描述符不存在时 <c>Descriptor(key)</c> 抛 <c>KeyNotFoundException</c>。
/// 于是一个「代码里写了、界面里能看见、但忘了在 <c>UiSettings.All</c> 注册」的键，
/// 会在真正读它的那一刻（往往是启动路径）炸掉 ——
/// 而这类故障在编译期与单元测试里都不会显形。
/// </para>
/// </summary>
public sealed class UiSettingsRegistrationTests
{
    /// <summary>
    /// 本组测试用到的键：每一个都必须已注册。
    ///
    /// <para>
    /// 这里刻意用**字面量**而不是引用 <c>UiSettings</c> 里的常量：
    /// 常量本身在 Core 里，改键名不会让测试变红，但旧键的消费者会静默炸掉。
    /// 字面量能钉住「磁盘上/代码里实际在用的那个键」。
    /// </para>
    /// </summary>
    public static TheoryData<string> ConsumedKeys() => new()
    {
        // 灰度（哀悼日）模式：UMango.App/Themes/GrayModeController.cs 的 GrayModeKey。
        // 读它的地方是 App.OnStartup → ApplyFromSettings（**启动路径**），
        // 写它的地方是设置页的灰度开关。
        "ui.appearance.grayMode",
    };

    [Theory]
    [MemberData(nameof(ConsumedKeys))]
    public void 已消费的设置键必须已注册(string key)
    {
        var service = new SettingsService(new MemorySettingsStore());
        UiSettings.RegisterAll(service);

        Assert.True(service.Has(key),
            $"设置键「{key}」被代码消费但未在 UiSettings.All 注册 —— " +
            "读取时会抛 KeyNotFoundException（启动路径上会直接崩）。");
    }

    [Fact]
    public void 所有描述符的键都不得重复()
    {
        var service = new SettingsService(new MemorySettingsStore());

        // RegisterAll 内部用 RegisterRange，重复键会抛 ArgumentException。
        // 这里显式再走一遍，把「重复」这件事钉成可读的失败。
        var duplicate = UiSettings.All
            .GroupBy(d => d.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        Assert.Null(duplicate);

        UiSettings.RegisterAll(service);
        Assert.Equal(UiSettings.All.Count, service.Descriptors.Count);
    }
}
