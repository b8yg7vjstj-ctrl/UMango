using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// YAML 键值文件。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/YamlFileProvider.cs</c>（Apache-2.0），
/// 依赖 YamlDotNet 18.1.0（MIT）。
///
/// **与 PCL 的三处实质差异**：
/// <list type="number">
///   <item>PCL 在目标 YAML 不存在时会尝试把同名的 <c>.json</c> 转换过来。
///         UMango 保留这个行为，但转换失败时**不吞掉异常**，而是发一个
///         <see cref="ConvertedFromLegacyFile"/> 事件把情况报出来 ——
///         PCL 那边只 <c>LogWrapper.Warn</c>，用户完全不知道自己丢了一段配置。</item>
///   <item>PCL 的 <c>Get</c> 在反序列化失败时把值改写成「布尔 true / 字符串原文」并
///         <b>立刻落盘</b>（同 <see cref="JsonFileProvider"/> 的数据损失问题）。
///         UMango 改成抛出，由存储层决定；不擅自改写用户数据。</item>
///   <item>PCL 的 <c>Get</c> 直接 <c>_rootNode.Children[key]</c>，键不存在时返回 null
///         然后在 <c>ConvertToEventStream()</c> 上抛 <see cref="ArgumentNullException"/>
///         —— 错误信息完全指不到"是哪个键不见了"。
///         UMango 改成抛 <see cref="KeyNotFoundException"/> 并带上键名。</item>
/// </list>
/// </summary>
public sealed class YamlFileProvider : CommonFileProvider, IEnumerableKeyProvider
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithEnforceRequiredMembers()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .Build();

    private readonly YamlMappingNode _rootNode;

    /// <summary>构造。文件不存在时尝试从同名 JSON 转换，仍不存在则用空节点。</summary>
    public YamlFileProvider(string path) : base(path)
    {
        var root = LoadFile(path);
        if (root is not null)
        {
            _rootNode = root;
            return;
        }

        // 目标文件不存在：尝试把同名的 JSON 转成 YAML（PCL 的兼容行为）。
        var dir = Path.GetDirectoryName(path);
        var legacyPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            Path.GetFileNameWithoutExtension(path) + ".json");

        if (File.Exists(legacyPath))
        {
            try
            {
                using var jsonStream = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var yamlStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                JsonToYamlConverter.Convert(jsonStream, yamlStream);
                var converted = LoadFile(path);
                if (converted is not null)
                {
                    _rootNode = converted;
                    LegacyFilePath = legacyPath;
                    ConvertedFromLegacyFile?.Invoke(legacyPath);
                    return;
                }
            }
            catch (Exception ex)
            {
                // 转换失败不能把整个配置文件变成"打不开"——退回空节点，
                // 但要让调用方知道发生了什么。
                ConversionFailed?.Invoke(legacyPath, ex);
            }
        }

        _rootNode = [];
    }

    /// <summary>
    /// 若本文件是从旧格式（同名 JSON）转换而来的，这里是那个旧文件的路径；否则为 null。
    ///
    /// 之所以同时提供属性和事件：转换发生在<b>构造函数里</b>，
    /// 调用方在构造之后才订阅事件就<b>永远收不到</b>那次通知。
    /// 属性不依赖订阅时机。
    /// </summary>
    public string? LegacyFilePath { get; private set; }

    /// <summary>成功从旧格式（JSON）转换过来时触发，参数为旧文件路径。</summary>
    public event Action<string>? ConvertedFromLegacyFile;

    /// <summary>转换失败时触发，参数为旧文件路径与异常。</summary>
    public event Action<string, Exception>? ConversionFailed;

    private static YamlMappingNode? LoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0) return [];
            var rootNode = yaml.Documents[0].RootNode;
            return rootNode as YamlMappingNode
                   ?? throw new ConfigFileInitException(path, $"Invalid root node type: {rootNode.NodeType}");
        }
        catch (Exception ex) when (ex is not ConfigFileInitException)
        {
            throw new ConfigFileInitException(path, "Failed to load YAML content", ex);
        }
    }

    /// <inheritdoc />
    public override T Get<T>(string key)
    {
        if (!_rootNode.Children.TryGetValue(key, out var result))
            throw new KeyNotFoundException($"Not found: '{key}'");

        var parser = result.ConvertToEventStream().GetParser();
        try
        {
            return Deserializer.Deserialize<T>(parser);
        }
        catch (Exception ex) when (ex is not KeyNotFoundException)
        {
            // 不擅自把坏值改写成一个"看起来合法"的值再落盘 —— 那是数据损失。
            throw new InvalidDataException($"配置项 '{key}' 的值无法反序列化为 {typeof(T).Name}", ex);
        }
    }

    /// <inheritdoc />
    public override void Set<T>(string key, T value)
    {
        var emitter = new YamlNodeEmitter();
        Serializer.Serialize(emitter, value);
        _rootNode.Children[key] = emitter.SingleRootNode;
    }

    /// <inheritdoc />
    public override bool Exists(string key) => _rootNode.Children.ContainsKey(key);

    /// <inheritdoc />
    public override void Remove(string key) => _rootNode.Children.Remove(key);

    /// <summary>全部键。</summary>
    public IEnumerable<string> Keys => _rootNode.Select(pair => pair.Key.ToString());

    /// <inheritdoc />
    protected override void WriteToStream(Stream stream)
    {
        var writer = new StreamWriter(stream, new UTF8Encoding(false));
        Serializer.Serialize(writer, _rootNode);
        writer.Flush();
    }
}
