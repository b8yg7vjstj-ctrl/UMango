using System.Text.Json;
using System.Text.Json.Nodes;
using UMango.Core.Utils;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// JSON 键值文件。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/JsonFileProvider.cs</c>（Apache-2.0）。
///
/// **修掉的 PCL 原版缺陷**：原版 <c>Get</c> 在 JSON 反序列化失败时会
/// <c>Set(key, fallback)</c> —— 那会把一个**已经写坏**的值悄悄改写成
/// 「布尔 true / 字符串原文」并立刻落盘，用户下次打开看到的值已经变了。
/// UMango 改成：读失败就**抛出**，由存储层决定怎么处理；不擅自改写用户数据。
/// </summary>
public sealed class JsonFileProvider : CommonFileProvider, IEnumerableKeyProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonCompat.SerializerOptions)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly JsonObject _root;

    /// <summary>构造。文件不存在时创建一个空对象（不立即落盘）。</summary>
    public JsonFileProvider(string path) : base(path)
    {
        if (!File.Exists(path))
        {
            _root = new JsonObject();
            return;
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var parsed = JsonNode.Parse(stream, JsonCompat.NodeOptions, JsonCompat.DocumentOptions);
            _root = parsed as JsonObject
                    ?? throw new ConfigFileInitException(path,
                        $"Invalid root element type: {parsed?.GetValueKind().ToString() ?? "Empty"}");
        }
        catch (Exception ex) when (ex is not ConfigFileInitException)
        {
            throw new ConfigFileInitException(path, "Failed to read JSON file", ex);
        }
    }

    /// <inheritdoc />
    public override T Get<T>(string key)
    {
        var node = _root[key];
        if (node is null) throw new KeyNotFoundException($"Not found: '{key}'");

        try
        {
            return node.Deserialize<T>(SerializerOptions)
                   ?? throw new InvalidDataException($"Deserialized value is null: '{key}'");
        }
        catch (JsonException ex)
        {
            // 不像 PCL 那样把坏值改写成一个"看起来合法"的值再落盘 —— 那是数据损失。
            throw new InvalidDataException($"配置项 '{key}' 的值无法反序列化为 {typeof(T).Name}", ex);
        }
    }

    /// <inheritdoc />
    public override void Set<T>(string key, T value) =>
        _root[key] = JsonSerializer.SerializeToNode(value, SerializerOptions);

    /// <inheritdoc />
    public override bool Exists(string key) => _root.ContainsKey(key);

    /// <inheritdoc />
    public override void Remove(string key) => _root.Remove(key);

    /// <summary>全部键。</summary>
    public IEnumerable<string> Keys => _root.Select(p => p.Key);

    /// <inheritdoc />
    protected override void WriteToStream(Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        _root.WriteTo(writer, SerializerOptions);
        writer.Flush();
    }
}

/// <summary>
/// 配置文件初始化失败。
/// </summary>
public sealed class ConfigFileInitException : Exception
{
    /// <summary>构造。</summary>
    public ConfigFileInitException(string path, string message, Exception? inner = null)
        : base(message, inner) => FilePath = path;

    /// <summary>出问题的文件路径。</summary>
    public string FilePath { get; }
}
