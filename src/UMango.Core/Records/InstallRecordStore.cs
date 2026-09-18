using System.Text.Json;
using System.Text.Json.Serialization;
using UMango.Core.PackageManagers;
using UMango.Core.Platform;

namespace UMango.Core.Records;

/// <summary>
/// 管理记录与次数记录的存储。
///
/// · <b>管理记录</b>：每次安装/卸载/升级的流水（含成败、耗时、错误码）；
/// · <b>次数记录</b>：按包源与总体的次数统计。
///
/// 落盘为 JSON，位置随平台而定（Windows 用 %APPDATA%，macOS/Linux 用 ~/.config）。
/// </summary>
public sealed class InstallRecordStore
{
    private const int MaxRecords = 1000;   // 只保留最近 N 条流水，避免无限膨胀

    private readonly List<InstallRecord> _records = [];
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>记录文件路径</summary>
    public static string StorePath
    {
        get
        {
            var dir = SystemInfo.IsWindows
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UMango")
                : Path.Combine(SystemInfo.HomeDirectory, ".config", "umango");
            return Path.Combine(dir, "install-records.json");
        }
    }

    /// <summary>全部记录（最近在前）</summary>
    public IReadOnlyList<InstallRecord> Records
    {
        get { lock (_gate) return _records.OrderByDescending(r => r.OccurredAt).ToList(); }
    }

    /// <summary>总次数记录</summary>
    public int TotalCount { get { lock (_gate) return _records.Count; } }
    public int SuccessCount { get { lock (_gate) return _records.Count(r => r.Success); } }
    public int FailureCount { get { lock (_gate) return _records.Count(r => !r.Success); } }

    /// <summary>按包源统计次数（界面上每个包管理器分类旁边显示用过多少次）</summary>
    public IReadOnlyDictionary<PackageSourceKind, int> CountBySource
    {
        get
        {
            lock (_gate)
            {
                return _records.GroupBy(r => r.Source)
                               .ToDictionary(g => g.Key, g => g.Count());
            }
        }
    }

    /// <summary>某包源的次数</summary>
    public int CountOf(PackageSourceKind kind) => CountBySource.TryGetValue(kind, out var n) ? n : 0;

    /// <summary>追加一条记录（自动裁剪并落盘）。</summary>
    public void Add(InstallRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > MaxRecords)
                _records.RemoveRange(0, _records.Count - MaxRecords);
        }
        Save();
    }

    /// <summary>从磁盘加载。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var json = File.ReadAllText(StorePath);
            var list = JsonSerializer.Deserialize<List<InstallRecord>>(json, JsonOpts);
            if (list is null) return;
            lock (_gate)
            {
                _records.Clear();
                _records.AddRange(list);
            }
        }
        catch (Exception)
        {
            // 记录文件损坏不应影响应用启动：忽略并继续（下次写入会覆盖）
        }
    }

    /// <summary>写入磁盘（先写临时文件再替换，避免写一半损坏）。</summary>
    public void Save()
    {
        try
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(Records.ToList(), JsonOpts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败不应中断业务：内存中的记录仍然可用
        }
    }

    /// <summary>清空（设置页提供）。</summary>
    public void Clear()
    {
        lock (_gate) _records.Clear();
        Save();
    }
}
