using UMango.Core.PackageManagers;

namespace UMango.Core.Caching;

/// <summary>
/// 包源可用性缓存。
///
/// 抽象出来的意义：把"缓存存哪里"与"什么时候该重新探测"分开。
/// 于是策略（TTL、过期降级）可以在不碰文件系统的前提下被单测覆盖，
/// 将来若要换成 SQLite 或系统凭据库也只替换实现。
/// </summary>
public interface IAvailabilityCache
{
    /// <summary>读取全部已缓存记录（含已过期的 —— 过期降级要用）。</summary>
    IReadOnlyList<CachedAvailability> Load();

    /// <summary>写入全部记录。</summary>
    void Save(IEnumerable<CachedAvailability> entries);
}

/// <summary>内存实现：测试用，也让"不落盘"成为可选项。</summary>
public sealed class MemoryAvailabilityCache : IAvailabilityCache
{
    private readonly Dictionary<PackageSourceKind, CachedAvailability> _entries = [];

    public int SaveCount { get; private set; }

    public IReadOnlyList<CachedAvailability> Load() => _entries.Values.ToList();

    public void Save(IEnumerable<CachedAvailability> entries)
    {
        SaveCount++;
        _entries.Clear();
        foreach (var e in entries) _entries[e.Kind] = e;
    }
}

/// <summary>
/// 文件实现：单个 JSON 文件，零新增依赖。
///
/// 为什么先用 JSON 而不是 SQLite（PCL-CE 用的是 SQLite 缓存服务）：
/// 引入 `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` 会打破
/// "不新增 NuGet 依赖"的约定，而这里的数据量极小（六个包源、每天更新一次），
/// 文件完全够用。将来若缓存内容变复杂，只要换掉本实现即可 —— 接口已经隔离好了。
/// 写入同样采用"先写临时文件再替换"，避免中途退出留下半截 JSON。
/// </summary>
public sealed class JsonAvailabilityCache(string path) : IAvailabilityCache
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string Path { get; } = path;

    public IReadOnlyList<CachedAvailability> Load()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return System.Text.Json.JsonSerializer.Deserialize<List<CachedAvailability>>(json, Options) ?? [];
        }
        catch (Exception)
        {
            // 缓存损坏不该拦住启动：当作没有缓存，下次探测后覆盖
            return [];
        }
    }

    public void Save(IEnumerable<CachedAvailability> entries)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(entries.ToList(), Options);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败不影响业务：内存里那份仍然有效
        }
    }
}
