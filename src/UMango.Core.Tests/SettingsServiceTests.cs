using System.Text.Json.Nodes;
using UMango.Core.Animation;
using UMango.Core.Configuration;
using Xunit;

namespace UMango.Core.Tests;

/// <summary>
/// 设置体系测试。重点是那些"写错就让用户设置全丢或界面显示错值"的地方：
/// 默认值、类型校验、范围校验、未知键容错、枚举往返、迁移。
/// </summary>
public class SettingsServiceTests
{
    private static SettingsService NewService(ISettingsStore? store = null, bool withCatalog = true)
    {
        var s = new SettingsService(store ?? new MemorySettingsStore());
        if (withCatalog) UiSettings.RegisterAll(s);
        return s;
    }

    // ================= 描述符自检 =================

    [Fact]
    public void 默认值类型与声明不符时注册即失败()
    {
        var s = NewService(withCatalog: false);
        var ex = Assert.Throws<ArgumentException>(() => s.Register(new SettingDescriptor
        {
            Key = "x", Section = "s", Title = "t",
            Kind = SettingKind.Bool,
            DefaultValue = 42              // 应该是 bool
        }));
        Assert.Contains("不匹配", ex.Message);
    }

    [Fact]
    public void 枚举默认值必须是该枚举的合法取值()
    {
        var s = NewService(withCatalog: false);
        Assert.Throws<ArgumentException>(() => s.Register(new SettingDescriptor
        {
            Key = "x", Section = "s", Title = "t",
            Kind = SettingKind.Enum,
            EnumType = typeof(AnimationDirectionMode),
            DefaultValue = 99              // 不是合法枚举值
        }));
    }

    [Fact]
    public void 键重复注册会立刻报错而不是静默覆盖()
    {
        var s = NewService(withCatalog: false);
        var d = new SettingDescriptor
        {
            Key = "dup", Section = "s", Title = "t",
            Kind = SettingKind.Bool, DefaultValue = false
        };
        s.Register(d);
        Assert.Throws<ArgumentException>(() => s.Register(d));
    }

    [Fact]
    public void 空键或空标题被拒绝()
    {
        var s = NewService(withCatalog: false);
        Assert.Throws<ArgumentException>(() => s.Register(new SettingDescriptor
        { Key = "", Section = "s", Title = "t", Kind = SettingKind.Bool, DefaultValue = false }));
        Assert.Throws<ArgumentException>(() => s.Register(new SettingDescriptor
        { Key = "k", Section = "s", Title = "", Kind = SettingKind.Bool, DefaultValue = false }));
    }

    // ================= 读写 =================

    [Fact]
    public void 未设置过时取到默认值()
    {
        var s = NewService();
        Assert.True(s.Get<bool>(UiSettings.AnimationEnabledKey));
        Assert.Equal(AnimationDirectionMode.Mirrored, s.Get<AnimationDirectionMode>(UiSettings.AnimationDirectionKey));
        Assert.Equal(59, s.Get<int>(UiSettings.AnimationFpsKey));
        Assert.False(s.Get<bool>(UiSettings.DebugModeKey));
    }

    [Fact]
    public void 写入后能读回()
    {
        var s = NewService();
        s.Set(UiSettings.AnimationEnabledKey, false);
        Assert.False(s.Get<bool>(UiSettings.AnimationEnabledKey));
    }

    [Fact]
    public void 枚举按整数落盘并能还原()
    {
        var store = new MemorySettingsStore();
        var s = NewService(store);
        s.Set(UiSettings.AnimationDirectionKey, AnimationDirectionMode.PclCompatible);
        s.Save();

        var reloaded = NewService(store);
        reloaded.Load();
        Assert.Equal(AnimationDirectionMode.PclCompatible,
            reloaded.Get<AnimationDirectionMode>(UiSettings.AnimationDirectionKey));
    }

    [Fact]
    public void 类型不符直接抛错而不是静默转换()
    {
        var s = NewService();
        Assert.Throws<ArgumentException>(() => s.Set(UiSettings.AnimationEnabledKey, 5));
        Assert.Throws<ArgumentException>(() => s.Set(UiSettings.ThemeKindKey, "不是枚举名"));
    }

    [Fact]
    public void 越界数值被拒绝_而不是静默夹住()
    {
        var s = NewService();
        // 静默夹住会掩盖界面 bug（例如滑块量程配错），所以这里要求明确报错
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Set(UiSettings.AnimationSpeedKey, 99.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Set(UiSettings.AnimationSpeedKey, 0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Set(UiSettings.AnimationFpsKey, 1000));
    }

    [Fact]
    public void 值没变时不触发变更通知()
    {
        var s = NewService();
        var fired = 0;
        s.Changed += _ => fired++;

        s.Set(UiSettings.AnimationEnabledKey, true);   // 本来就是 true
        Assert.Equal(0, fired);

        s.Set(UiSettings.AnimationEnabledKey, false);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void 变更通知带上是哪个键()
    {
        var s = NewService();
        string? changedKey = null;
        s.Changed += k => changedKey = k;
        s.Set(UiSettings.DebugModeKey, true);
        Assert.Equal(UiSettings.DebugModeKey, changedKey);
    }

    // ================= 默认值判定与重置 =================

    [Fact]
    public void 能判断单项与整组是否已偏离默认()
    {
        var s = NewService();
        Assert.False(s.IsChanged(UiSettings.AnimationEnabledKey));
        Assert.True(s.IsSectionDefault(UiSettings.SectionAnimation));

        s.Set(UiSettings.AnimationSpeedKey, 1.5);
        Assert.True(s.IsChanged(UiSettings.AnimationSpeedKey));
        Assert.False(s.IsSectionDefault(UiSettings.SectionAnimation));
        // 别的分组不受影响
        Assert.True(s.IsSectionDefault(UiSettings.SectionAppearance));
    }

    [Fact]
    public void 重置单项与整组()
    {
        var s = NewService();
        s.Set(UiSettings.AnimationSpeedKey, 1.5);
        s.Set(UiSettings.AnimationEnabledKey, false);

        s.Reset(UiSettings.AnimationSpeedKey);
        Assert.Equal(1.0, s.Get<double>(UiSettings.AnimationSpeedKey));
        Assert.False(s.Get<bool>(UiSettings.AnimationEnabledKey));   // 另一项不受影响

        s.ResetSection(UiSettings.SectionAnimation);
        Assert.True(s.IsSectionDefault(UiSettings.SectionAnimation));
    }

    [Fact]
    public void 重置全部()
    {
        var s = NewService();
        s.Set(UiSettings.AnimationSpeedKey, 1.5);
        s.Set(UiSettings.DebugModeKey, true);
        s.ResetAll();

        Assert.True(s.IsSectionDefault(UiSettings.SectionAnimation));
        Assert.True(s.IsSectionDefault(UiSettings.SectionDebug));
    }

    // ================= 落盘与容错 =================

    [Fact]
    public void 默认值不落盘_保持文件精简()
    {
        var store = new MemorySettingsStore();
        var s = NewService(store);
        s.Save();   // 什么都没改

        var saved = store.Load();
        Assert.False(saved.ContainsKey(UiSettings.AnimationSpeedKey));
        // 但架构版本必须写进去，迁移要用
        Assert.True(saved.ContainsKey("__schemaVersion"));
    }

    [Fact]
    public void 保存载入往返()
    {
        var store = new MemorySettingsStore();
        var a = NewService(store);
        a.Set(UiSettings.AnimationSpeedKey, 1.5);
        a.Set(UiSettings.DebugModeKey, true);
        a.Set(UiSettings.ThemeKindKey, ThemeFamily.Fluent10);
        a.Save();

        var b = NewService(store);
        b.Load();

        Assert.Equal(1.5, b.Get<double>(UiSettings.AnimationSpeedKey));
        Assert.True(b.Get<bool>(UiSettings.DebugModeKey));
        Assert.Equal(ThemeFamily.Fluent10, b.Get<ThemeFamily>(UiSettings.ThemeKindKey));
    }

    [Fact]
    public void 未知键被忽略_不会让整份配置失效()
    {
        var store = new MemorySettingsStore();
        store.Save(new Dictionary<string, object?>
        {
            ["__schemaVersion"] = 1,
            ["某个旧版本的键"] = true,
            [UiSettings.DebugModeKey] = true,
        });

        var s = NewService(store);
        s.Load();

        // 认识的键照常读回，不认识的静默忽略
        Assert.True(s.Get<bool>(UiSettings.DebugModeKey));
        Assert.True(s.Has(UiSettings.DebugModeKey));
        Assert.False(s.Has("某个旧版本的键"));
    }

    [Fact]
    public void 单项非法值被丢弃但不影响其它项()
    {
        var store = new MemorySettingsStore();
        store.Save(new Dictionary<string, object?>
        {
            ["__schemaVersion"] = 1,
            [UiSettings.AnimationFpsKey] = 99999,        // 越界
            [UiSettings.DebugModeKey] = true,            // 正常
        });

        var s = NewService(store);
        s.Load();   // 不应抛异常

        Assert.Equal(59, s.Get<int>(UiSettings.AnimationFpsKey));   // 退回默认值
        Assert.True(s.Get<bool>(UiSettings.DebugModeKey));          // 其它项照常
    }

    [Fact]
    public void 存储不可用时不拦住启动()
    {
        var s = NewService(new ThrowingStore());
        s.Load();   // 不应抛异常
        Assert.True(s.Get<bool>(UiSettings.AnimationEnabledKey));
    }

    [Fact]
    public void JSON文件存储往返_含中文键值()
    {
        var path = Path.Combine(Path.GetTempPath(), $"umango-set-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFileSettingsStore(path);
            var a = NewService(store);
            a.Set(UiSettings.AnimationSpeedKey, 1.25);
            a.Set(UiSettings.DebugModeKey, true);
            a.Save();

            Assert.True(File.Exists(path));
            var b = NewService(new JsonFileSettingsStore(path));
            b.Load();

            Assert.Equal(1.25, b.Get<double>(UiSettings.AnimationSpeedKey));
            Assert.True(b.Get<bool>(UiSettings.DebugModeKey));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void 迁移会写入架构版本()
    {
        var s = NewService();
        var migrated = s.Migrate(new JsonObject());

        Assert.Equal(SettingsService.SchemaVersion, migrated["__schemaVersion"]!.GetValue<int>());
    }

    // ================= 目录与动画系统的一致性 =================

    [Fact]
    public void 设置目录的动画默认值必须与动画系统一致()
    {
        // 这是最容易悄悄漂移的地方：两处各写一份默认值，
        // 一旦不一致，设置页会显示一个"看起来是默认、其实不是"的值。
        var defaults = new AnimationSettings();
        var s = NewService();

        Assert.Equal(defaults.Enabled, s.Get<bool>(UiSettings.AnimationEnabledKey));
        Assert.Equal(defaults.Direction, s.Get<AnimationDirectionMode>(UiSettings.AnimationDirectionKey));
        Assert.Equal(defaults.SpeedFactor, s.Get<double>(UiSettings.AnimationSpeedKey));
        Assert.Equal(defaults.FpsLimit, s.Get<int>(UiSettings.AnimationFpsKey));
    }

    [Fact]
    public void 设置目录自洽_键唯一且分组齐全()
    {
        var all = UiSettings.All;
        Assert.Equal(all.Count, all.Select(d => d.Key).Distinct().Count());
        Assert.All(all, d => d.Validate());   // 每个描述符都要自洽

        var sections = all.Select(d => d.Section).Distinct().ToList();
        foreach (var expected in UiSettings.SectionOrder)
            Assert.Contains(expected, sections);
    }

    [Fact]
    public void 描述符按分组与顺序排好_设置页可直接渲染()
    {
        var s = NewService();
        var inAnimation = s.Descriptors.Where(d => d.Section == UiSettings.SectionAnimation).ToList();

        Assert.Equal(4, inAnimation.Count);
        // 同一分组内按 Order 升序
        var orders = inAnimation.Select(d => d.Order).ToList();
        Assert.Equal(orders.OrderBy(x => x), orders);
        Assert.Equal(4, s.Sections.Count());
    }

    private sealed class ThrowingStore : ISettingsStore
    {
        public IReadOnlyDictionary<string, object?> Load() => throw new InvalidOperationException("存储不可用");
        public void Save(IReadOnlyDictionary<string, object?> values) => throw new InvalidOperationException("存储不可用");
    }
}
