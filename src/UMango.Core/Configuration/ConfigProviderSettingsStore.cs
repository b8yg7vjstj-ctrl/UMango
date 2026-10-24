using UMango.Core.Configuration.Storage;

namespace UMango.Core.Configuration;

/// <summary>
/// 把 PCL 风格的存取仓库（<see cref="IConfigProvider"/>）适配成 UMango 的设置存储。
///
/// 这是 P0-2 两套配置的**接缝**：UMango 现有的 <see cref="SettingsService"/> 用
/// <see cref="ISettingsStore"/> 读写「一次性全部键值」，
/// 而 PCL-CE 的存取仓库模型是「按键读写单个值」。
/// 有了这个适配器，设置服务就能直接用 JSON / YAML / INI / 加密四种后端，
/// 不必为每种格式重写一遍设置逻辑。
///
/// 注意它与 <see cref="JsonFileSettingsStore"/> 的差别：
/// 后者一次读整份 JSON 一次性写整份；这里按需读写单键，
/// 适合"很多键、只有几个被改过"的场景，也天然支持加密后端。
/// </summary>
public sealed class ConfigProviderSettingsStore : ISettingsStore
{
    private readonly IConfigProvider _provider;

    /// <summary>构造。</summary>
    public ConfigProviderSettingsStore(IConfigProvider provider) => _provider = provider;

    /// <summary>底层存取仓库。</summary>
    public IConfigProvider Provider => _provider;

    /// <summary>
    /// 列出全部键。
    /// PCL 的存取仓库不一定支持枚举键（<c>IEnumerableKeyProvider</c> 是可选能力），
    /// 所以这里只在底层支持时返回；不支持时返回空集合（而不是抛错）。
    /// </summary>
    public IReadOnlyList<string> EnumerateKeys()
    {
        if (_provider is IEnumerableKeyProvider enumerable)
            return enumerable.Keys.ToList();
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Load()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in EnumerateKeys())
        {
            try
            {
                if (_provider.GetValue<object?>(key, out var value)) result[key] = Normalize(value);
            }
            catch (Exception)
            {
                // 单个键读不出来（类型对不上 / 文件损坏）不该让整份配置失效。
            }
        }
        return result;
    }

    /// <summary>
    /// 把底层存回来的值还原成基元类型。
    ///
    /// JSON 后端用 <c>object?</c> 取值时拿到的是 <see cref="System.Text.Json.JsonElement"/>，
    /// 不是一个真正的 int/bool —— 直接塞进 <see cref="SettingsService"/> 会在
    /// 类型归一化时炸掉（"Unable to cast JsonElement to IConvertible"）。
    /// </summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        System.Text.Json.JsonElement element => NormalizeElement(element),
        _ => value
    };

    private static object? NormalizeElement(System.Text.Json.JsonElement element) =>
        element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Undefined => null,
            System.Text.Json.JsonValueKind.Number =>
                element.TryGetInt64(out var l) ? l :
                element.TryGetDouble(out var d) ? d : element.GetRawText(),
            System.Text.Json.JsonValueKind.String => element.GetString(),
            _ => element.GetRawText()
        };

    /// <inheritdoc />
    public void Save(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var (key, value) in values)
            _provider.SetValue(key, value);
    }
}
