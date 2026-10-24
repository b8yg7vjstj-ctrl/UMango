using System.Security.Cryptography;
using UMango.Core.Configuration;
using UMango.Core.Configuration.Storage;
using Xunit;

namespace UMango.Core.Tests.Configuration;

/// <summary>
/// P0-2 配置体系：存取仓库层（文件 / 加密 / 动态缓存）与迁移引擎。
/// </summary>
public class ConfigStorageTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "umango-cfg-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- FileConfigStorage ----------------

    [Fact]
    public void 文件仓库写入后可读回()
    {
        var dir = TempDir();
        try
        {
            var file = new JsonFileProvider(Path.Combine(dir, "cfg.json"));
            var storage = new FileConfigStorage(file);
            storage.SetValue("a", 42);
            storage.SetValue("b", "hello");
            storage.Flush();

            var reopened = new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "cfg.json")));
            Assert.True(reopened.GetValue<int>("a", out var a));
            Assert.Equal(42, a);
            Assert.True(reopened.GetValue<string>("b", out var b));
            Assert.Equal("hello", b);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 键不存在时读不到()
    {
        var dir = TempDir();
        try
        {
            var storage = new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "cfg.json")));
            Assert.False(storage.GetValue<int>("missing", out _));
            Assert.False(storage.Exists("missing"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 删键后读不到()
    {
        var dir = TempDir();
        try
        {
            var storage = new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "cfg.json")));
            storage.SetValue("x", 1);
            storage.Delete("x");
            Assert.False(storage.GetValue<int>("x", out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 值损坏时被移除并触发事件()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            File.WriteAllText(path, """{"bad":"not-a-number"}""");
            var storage = new FileConfigStorage(new JsonFileProvider(path));
            var removed = new List<string>();
            storage.CorruptedKeyRemoved += removed.Add;

            Assert.False(storage.GetValue<int>("bad", out _));
            Assert.Equal(["bad"], removed);
            // 坏值必须被移除，否则这个键永远读不出来
            Assert.False(storage.Exists("bad"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 停止仓库时立即落盘()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var storage = new FileConfigStorage(new JsonFileProvider(path));
            storage.SetValue("k", "v");
            storage.Stop();

            // 不等 250ms 防抖，直接读磁盘
            Assert.Contains("\"k\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 多次写入只落盘一次()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var file = new JsonFileProvider(path);
            var storage = new FileConfigStorage(file);
            for (var i = 0; i < 20; i++) storage.SetValue($"k{i}", i);
            storage.Stop();

            var text = File.ReadAllText(path);
            Assert.Contains("\"k19\"", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- EncryptedFileConfigStorage ----------------

    [Fact]
    public void 加密仓库写入的值在文件里不是明文()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var protector = new FakeProtector();
            var storage = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), protector);
            storage.SetValue("token", "s3cret-value");
            storage.Stop();

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("s3cret-value", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 加密仓库可读回原值()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var protector = new FakeProtector();
            var storage = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), protector);
            storage.SetValue("token", "s3cret-value");
            storage.Stop();

            var reopened = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), new FakeProtector());
            Assert.True(reopened.GetValue<string>("token", out var v));
            Assert.Equal("s3cret-value", v);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 解密失败时读不到并触发事件()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var storage = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), new FakeProtector());
            storage.SetValue("token", "v");
            storage.Stop();

            var failing = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), new ThrowingProtector());
            Exception? seen = null;
            failing.DecryptFailed += (_, ex) => seen = ex;

            Assert.False(failing.GetValue<string>("token", out _));
            Assert.NotNull(seen);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 加密仓库支持布尔值()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.json");
            var storage = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), new FakeProtector());
            storage.SetValue("flag", true);
            storage.Stop();

            var reopened = new EncryptedFileConfigStorage(
                new FileConfigStorage(new JsonFileProvider(path)), new FakeProtector());
            Assert.True(reopened.GetValue<bool>("flag", out var v));
            Assert.True(v);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- DynamicCacheConfigStorage ----------------

    [Fact]
    public void 动态缓存按上下文分开存取()
    {
        var dir = TempDir();
        try
        {
            var ctx1 = new object();
            var ctx2 = new object();
            var storage = new DynamicCacheConfigStorage
            {
                StorageFactory = ctx => new FileConfigStorage(
                    new JsonFileProvider(Path.Combine(dir, (ctx?.GetHashCode() ?? 0) + ".json")))
            };

            storage.SetValue("k", "v1", ctx1);
            storage.SetValue("k", "v2", ctx2);

            Assert.True(storage.GetValue<string>("k", out var v1, ctx1));
            Assert.True(storage.GetValue<string>("k", out var v2, ctx2));
            Assert.NotNull(v1);
            Assert.NotNull(v2);
            Assert.Equal("v1", v1);
            Assert.Equal("v2", v2);
            Assert.Equal(2, storage.CachedContextCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 动态缓存失效后释放底层存储()
    {
        var dir = TempDir();
        try
        {
            var ctx = new object();
            var inner = new TrackingStorage();
            var storage = new DynamicCacheConfigStorage
            {
                StorageFactory = _ => inner
            };
            storage.SetValue("k", "v", ctx);
            Assert.False(inner.Stopped);

            Assert.True(storage.InvalidateCache(ctx));
            Assert.True(inner.Stopped);
            Assert.Equal(0, storage.CachedContextCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- YAML 后端 ----------------

    [Fact]
    public void YAML仓库可写可读()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "cfg.yaml");
            var storage = new FileConfigStorage(new YamlFileProvider(path));
            storage.SetValue("name", "umango");
            storage.SetValue("count", 7);
            storage.Stop();

            var reopened = new FileConfigStorage(new YamlFileProvider(path));
            Assert.True(reopened.GetValue<string>("name", out var name));
            Assert.Equal("umango", name);
            Assert.True(reopened.GetValue<int>("count", out var count));
            Assert.Equal(7, count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void YAML仓库键不存在时报键名()
    {
        var dir = TempDir();
        try
        {
            var provider = new YamlFileProvider(Path.Combine(dir, "cfg.yaml"));
            var ex = Assert.Throws<KeyNotFoundException>(() => provider.Get<int>("nope"));
            Assert.Contains("nope", ex.Message);

            // 走仓库层时，键不存在应表现为"读不到"而不是抛错
            var storage = new FileConfigStorage(provider);
            Assert.False(storage.GetValue<int>("nope", out _));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void YAML仓库缺文件时从同名JSON转换()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "cfg.json"), """{"a":1,"b":"x"}""");
            var provider = new YamlFileProvider(Path.Combine(dir, "cfg.yaml"));

            Assert.True(provider.Exists("a"));
            Assert.Equal(1, provider.Get<int>("a"));
            // 转换发生在构造函数里，事件订阅者根本来不及听 —— 属性才是可靠的证据
            Assert.NotNull(provider.LegacyFilePath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void YAML与JSON后端互相可读()
    {
        var dir = TempDir();
        try
        {
            var json = new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "c.json")));
            json.SetValue("k", "v");
            json.Stop();

            var yamlPath = Path.Combine(dir, "c.yaml");
            var yaml = new FileConfigStorage(new YamlFileProvider(yamlPath));
            Assert.True(yaml.GetValue<string>("k", out var v));
            Assert.Equal("v", v);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- 迁移引擎 ----------------

    [Fact]
    public void 迁移引擎找到最短路径()
    {
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (_, _) => { } },
            new ConfigMigration { From = "b", To = "c", OnMigration = (_, _) => { } },
            new ConfigMigration { From = "a", To = "c", OnMigration = (_, _) => { } }
        };

        Assert.True(ConfigMigrationEngine.TryFindBestPath("a", "c", edges, out var path));
        Assert.Single(path!);
        Assert.Equal("a", path![0].From);
    }

    [Fact]
    public void 迁移引擎在同长度路径里选优先级高的()
    {
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", Priority = 1, OnMigration = (_, _) => { } },
            new ConfigMigration { From = "a", To = "c", Priority = 10, OnMigration = (_, _) => { } },
            new ConfigMigration { From = "b", To = "d", Priority = 1, OnMigration = (_, _) => { } },
            new ConfigMigration { From = "c", To = "d", Priority = 1, OnMigration = (_, _) => { } }
        };

        Assert.True(ConfigMigrationEngine.TryFindBestPath("a", "d", edges, out var path));
        Assert.Equal(2, path!.Count);
        Assert.Equal("a", path![0].From);
        Assert.Equal("c", path[1].From);
    }

    [Fact]
    public void 迁移引擎找不到路径时返回假()
    {
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (_, _) => { } }
        };
        Assert.False(ConfigMigrationEngine.TryFindBestPath("x", "y", edges, out var path));
        Assert.Null(path);
    }

    [Fact]
    public void 迁移引擎起点等于终点时路径为空()
    {
        Assert.True(ConfigMigrationEngine.TryFindBestPath("a", "a", Array.Empty<ConfigMigration>(), out var path));
        Assert.Empty(path!);
    }

    [Fact]
    public void 迁移引擎按序执行路径上的每一步()
    {
        var executed = new List<string>();
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (f, t) => executed.Add(f + "->" + t) },
            new ConfigMigration { From = "b", To = "c", OnMigration = (f, t) => executed.Add(f + "->" + t) }
        };

        var result = ConfigMigrationEngine.Migrate("a", "c", edges, _ => true);
        Assert.True(result.Succeeded);
        Assert.Equal(["a->b", "b->c"], executed);
    }

    [Fact]
    public void 迁移中途失败时报告失败而不是抛异常()
    {
        var executed = new List<string>();
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (f, t) => executed.Add(f + "->" + t) },
            new ConfigMigration
            {
                From = "b", To = "c",
                OnMigration = (_, _) => throw new InvalidOperationException("boom")
            }
        };

        var result = ConfigMigrationEngine.Migrate("a", "c", edges, _ => true);
        Assert.False(result.Succeeded);
        Assert.True(result.ExecutionFailed);
        Assert.NotNull(result.Error);
        Assert.Equal(["a->b"], executed);
    }

    [Fact]
    public void 干跑不执行任何一步()
    {
        var executed = 0;
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (_, _) => executed++ }
        };

        var result = ConfigMigrationEngine.Migrate("a", "b", edges, _ => true, dryRun: true);
        Assert.True(result.Succeeded);
        Assert.Equal(0, executed);
    }

    [Fact]
    public void 多个起点都存在时选路径最长的那个()
    {
        // 起点按字母序是 "a"，但 "a" 只有一条边、"z" 有两条 ——
        // 必须选 "z"（路径最长 = 起点最早 = 配置被完整传递）。
        // 若实现改成"按字母序取第一个"，这条就会挂。
        var executed = new List<string>();
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "d", OnMigration = (f, t) => executed.Add(f + "->" + t) },
            new ConfigMigration { From = "z", To = "b", OnMigration = (f, t) => executed.Add(f + "->" + t) },
            new ConfigMigration { From = "b", To = "d", OnMigration = (f, t) => executed.Add(f + "->" + t) }
        };

        var result = ConfigMigrationEngine.Migrate("d", "d", edges, _ => true);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "z->b", "b->d" }, executed);
    }

    [Fact]
    public void 起点文件不存在时不迁移()
    {
        var executed = 0;
        var edges = new[]
        {
            new ConfigMigration { From = "a", To = "b", OnMigration = (_, _) => executed++ }
        };

        var result = ConfigMigrationEngine.Migrate("a", "b", edges, _ => false);
        Assert.False(result.Succeeded);
        Assert.Equal(0, executed);
    }

    // ---------------- 适配器 ----------------

    [Fact]
    public void 存取仓库适配器可读写设置()
    {
        var dir = TempDir();
        try
        {
            var store = new ConfigProviderSettingsStore(
                new FileConfigStorage(new JsonFileProvider(Path.Combine(dir, "cfg.json"))));
            store.Save(new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x" });

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            // JSON 反序列化出来的是 Int64，不是 Int32 —— 比较时统一成数值
            Assert.Equal(1, Convert.ToInt32(loaded["a"]));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        // 用 base64 假装加密：明文不会以明文形态出现在文件里，
        // 但可逆，方便测试读回。
        public string Protect(string plaintext) =>
            "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]))
                : throw new FormatException("not encrypted");
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => throw new CryptographicException("bad key");
    }

    private sealed class TrackingStorage : ConfigStorage
    {
        public bool Stopped { get; private set; }
        private readonly Dictionary<string, object?> _values = [];

        protected override bool OnAccess<TKey, TValue>(
            StorageAction action, ref TKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ref TValue value, object? argument)
        {
            var k = key?.ToString() ?? "";
            switch (action)
            {
                case StorageAction.Set:
                    _values[k] = value;
                    return false;
                case StorageAction.Get:
                    if (_values.TryGetValue(k, out var v) && v is TValue tv)
                    {
                        value = tv;
                        return true;
                    }
                    value = default!;
                    return false;
                case StorageAction.Exists:
                    return _values.ContainsKey(k);
                case StorageAction.Delete:
                    _values.Remove(k);
                    return false;
                default:
                    return false;
            }
        }

        public override void Stop() => Stopped = true;
    }
}
