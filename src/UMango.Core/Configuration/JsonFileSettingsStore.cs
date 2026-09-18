using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace UMango.Core.Configuration;

/// <summary>
/// 把设置存成 JSON 文件。
///
/// 写入采用「先写临时文件再替换」：直接覆盖时若进程中途退出，
/// 用户会得到半截 JSON —— 表现为"所有设置突然回到默认值"，很难排查。
/// </summary>
public sealed class JsonFileSettingsStore(string path) : ISettingsStore
{
    // .NET 10 下把 JsonSerializerOptions 传给 JsonNode.ToJsonString 时，
    // **必须**带 TypeInfoResolver，否则抛
    // "JsonSerializerOptions instance must specify a TypeInfoResolver setting
    //  before being marked as read-only"。ToJsonString 也只有接受 JsonSerializerOptions 的重载，
    // 所以这里显式给一个默认解析器。
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>设置文件路径</summary>
    public string Path { get; } = path;

    public IReadOnlyDictionary<string, object?> Load()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!File.Exists(Path)) return result;

        var text = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(text)) return result;

        if (JsonNode.Parse(text) is not JsonObject root) return result;

        foreach (var (key, node) in root)
        {
            result[key] = ToClr(node);
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<string, object?> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = JsonValue.Create(value);
        }

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(WriteOpts));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>
    /// 把 JSON 节点还原成 CLR 基元。
    /// 这里只认基元类型：设置值只有 bool/int/double/string 与枚举（落盘为 int），
    /// 复杂对象不该出现在设置里 —— 出现了说明设计有问题，而不是需要更聪明的转换器。
    /// </summary>
    private static object? ToClr(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s)) return s;
        return value.ToJsonString();
    }

    /// <summary>导出并跑一遍迁移（供诊断页/备份使用）。</summary>
    public JsonObject LoadRawWithMigration(Func<JsonObject, JsonObject> migrate)
    {
        if (!File.Exists(Path)) return new JsonObject();
        if (JsonNode.Parse(File.ReadAllText(Path)) is not JsonObject root) return new JsonObject();
        return migrate(root);
    }
}
