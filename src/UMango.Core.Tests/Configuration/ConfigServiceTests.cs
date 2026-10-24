using System.Text.Json;
using UMango.Core.Configuration;
using UMango.Core.Configuration.Storage;

namespace UMango.Core.Tests.Configuration;

/// <summary>
/// P0-2 配置体系：<see cref="ConfigService"/> 门面 + <see cref="ConfigEventHub"/> 与
/// <see cref="SettingsService"/> 的接线。
/// </summary>
public class ConfigServiceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- ConfigService 门面 ----------------

    [Fact]
    public void 注册来源后可读写()
    {
        var dir = TempDir();
        try
        {
            using var service = new ConfigService();
            service.RegisterProvider(ConfigSource.Shared,
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "shared.json"))));

            service.SetValue("a", 1);
            Assert.True(service.TryGetValue<int>("a", out var v));
            Assert.Equal(1, v);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 未注册的来源抛异常()
    {
        using var service = new ConfigService();
        Assert.Throws<InvalidOperationException>(() => service.GetProvider(ConfigSource.Local));
    }

    [Fact]
    public void 本地没有时回退到共享()
    {
        var dir = TempDir();
        try
        {
            using var service = new ConfigService();
            service.RegisterProvider(ConfigSource.Shared,
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "shared.json"))));
            service.RegisterProvider(ConfigSource.Local,
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "local.json"))));

            service.SetValue("theme", "dark", ConfigSource.Shared);

            Assert.True(service.TryGetValue<string>("theme", out var v, ConfigSource.Local));
            Assert.Equal("dark", v);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 键存在性也回退到共享()
    {
        var dir = TempDir();
        try
        {
            using var service = new ConfigService();
            service.RegisterProvider(ConfigSource.Shared,
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "shared.json"))));
            service.RegisterProvider(ConfigSource.Local,
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "local.json"))));

            service.SetValue("k", 1, ConfigSource.Shared);
            Assert.True(service.Exists("k", ConfigSource.Local));
            Assert.False(service.Exists("missing", ConfigSource.Local));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 损坏的配置文件被挪到备份并记录()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "broken.json");
            File.WriteAllText(path, "{ this is not json ");

            using var service = new ConfigService();
            var ok = service.TryInitializeFile(path);

            Assert.False(ok);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".failbackup"));
            Assert.Contains(path + ".failbackup", service.RecoveredFiles);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 损坏的配置文件能用bak恢复()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "broken.json");
            File.WriteAllText(path, "{ broken");
            File.WriteAllText(path + ".bak", "{\"good\":1}");

            using var service = new ConfigService();
            var ok = service.TryInitializeFile(path);

            Assert.True(ok);
            Assert.True(File.Exists(path + ".failbackup"));
            var restored = File.ReadAllText(path);
            Assert.Contains("good", restored);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 完好的配置不需要恢复()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "ok.json");
            File.WriteAllText(path, "{\"a\":1}");

            using var service = new ConfigService();
            Assert.True(service.TryInitializeFile(path));
            Assert.True(File.Exists(path));
            Assert.Empty(service.RecoveredFiles);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 文件不存在不算错误()
    {
        var dir = TempDir();
        try
        {
            using var service = new ConfigService();
            Assert.True(service.TryInitializeFile(Path.Combine(dir, "nope.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 目标已存在时不迁移()
    {
        var dir = TempDir();
        try
        {
            var target = Path.Combine(dir, "target.json");
            File.WriteAllText(target, "{}");
            var source = Path.Combine(dir, "old.json");
            File.WriteAllText(source, "{\"x\":1}");

            using var service = new ConfigService();
            var executed = 0;
            var result = service.Migrate(target, new[]
            {
                new ConfigMigration { From = source, To = target, OnMigration = (_, _) => executed++ }
            });

            Assert.False(result.Succeeded);
            Assert.Equal(0, executed);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- 事件总线接线 ----------------

    [Fact]
    public void 预览观察者可以改写写入的值()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.flag", Section = "测试", Title = "标志", Kind = SettingKind.Bool, DefaultValue = false });

        hub.Observe("t.flag", new ConfigObserver(ConfigEvent.Set,
            e => e.NewValueReplacement = true, IsPreview: true));

        service.Set("t.flag", false);
        Assert.True(service.Get<bool>("t.flag"));
    }

    [Fact]
    public void 预览观察者可以否决写入()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.flag", Section = "测试", Title = "标志", Kind = SettingKind.Bool, DefaultValue = false });

        hub.Observe("t.flag", new ConfigObserver(ConfigEvent.Set,
            e => e.Cancelled = true, IsPreview: true));

        service.Set("t.flag", true);
        Assert.False(service.Get<bool>("t.flag"));
    }

    [Fact]
    public void 否决写入时不触发变更通知()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.flag", Section = "测试", Title = "标志", Kind = SettingKind.Bool, DefaultValue = false });

        var changed = new List<string>();
        service.Changed += changed.Add;

        hub.Observe("t.flag", new ConfigObserver(ConfigEvent.Set,
            e => e.Cancelled = true, IsPreview: true));

        service.Set("t.flag", true);
        Assert.Empty(changed);
    }

    [Fact]
    public void 普通观察者不能改写值()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.flag", Section = "测试", Title = "标志", Kind = SettingKind.Bool, DefaultValue = false });

        // 非预览观察者写替换值 —— 必须无效
        hub.Observe("t.flag", new ConfigObserver(ConfigEvent.Set,
            e => e.NewValueReplacement = true, IsPreview: false));

        service.Set("t.flag", false);
        Assert.False(service.Get<bool>("t.flag"));
    }

    [Fact]
    public void 预览观察者可以替换读到的值()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.num", Section = "测试", Title = "数字", Kind = SettingKind.Int, DefaultValue = 5 });

        hub.Observe("t.num", new ConfigObserver(ConfigEvent.Get,
            e => e.NewValueReplacement = 99, IsPreview: true));

        Assert.Equal(99, service.Get<int>("t.num"));
    }

    [Fact]
    public void 重置可以被否决()
    {
        var hub = new ConfigEventHub();
        var service = new SettingsService(new MemorySettingsStore(), hub);
        service.Register(new SettingDescriptor { Key = "t.num", Section = "测试", Title = "数字", Kind = SettingKind.Int, DefaultValue = 5 });
        service.Set("t.num", 10);

        hub.Observe("t.num", new ConfigObserver(ConfigEvent.Reset,
            e => e.Cancelled = true, IsPreview: true));

        service.Reset("t.num");
        Assert.Equal(10, service.Get<int>("t.num"));
    }

    [Fact]
    public void 没有事件总线时行为不变()
    {
        var service = new SettingsService(new MemorySettingsStore());
        service.Register(new SettingDescriptor { Key = "t.flag", Section = "测试", Title = "标志", Kind = SettingKind.Bool, DefaultValue = false });
        service.Set("t.flag", true);
        Assert.True(service.Get<bool>("t.flag"));
    }
}
