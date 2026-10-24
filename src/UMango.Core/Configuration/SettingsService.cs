using System.Text.Json;
using System.Text.Json.Nodes;

namespace UMango.Core.Configuration;

/// <summary>
/// 设置服务：注册描述符、读写值、变更通知、默认值判定、迁移与落盘。
///
/// 相比原来那套"手写一个 JSON 节点、每个设置单独读写"的做法（见 AppSettings 的历史），
/// 这里的关键差异是**描述符驱动**：
///   · 设置项集中声明，界面按描述符生成，新增设置不必三处改；
///   · 默认值与"是否已偏离默认"由服务统一判定（PCL 的 IConfigScope.IsDefault 同义）；
///   · 取值统一做范围/类型校验，越界值不会渗进业务。
/// </summary>
public sealed class SettingsService
{
    /// <summary>当前配置架构版本。结构不兼容变更时 +1，并补一条迁移。</summary>
    public const int SchemaVersion = 1;

    private const string VersionKey = "__schemaVersion";

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private readonly Dictionary<string, SettingDescriptor> _descriptors = [];
    private readonly Dictionary<string, object?> _values = [];
    private readonly List<Func<JsonObject, JsonObject>> _migrations = [];
    private readonly ISettingsStore _store;
    private readonly ConfigEventHub? _events;

    public SettingsService(ISettingsStore store) => _store = store;

    /// <summary>
    /// 构造，并接入配置事件总线。
    ///
    /// 事件总线是<b>可选</b>的：不接的话设置服务行为完全不变（只发 <see cref="Changed"/>）。
    /// 接了之后，读写会先走事件总线的「预览」段（观察者可以改写值或否决操作），
    /// 再走「普通」段（只做通知）。语义与 PCL-CE 的 <c>ConfigItem._TriggerEvent</c> 一致。
    /// </summary>
    public SettingsService(ISettingsStore store, ConfigEventHub events) : this(store) =>
        _events = events;

    /// <summary>某个设置变化时触发（参数为键名）。界面订阅它做即时生效。</summary>
    public event Action<string>? Changed;

    /// <summary>全部描述符（按分组与顺序排好），设置页直接用它生成界面。</summary>
    public IReadOnlyList<SettingDescriptor> Descriptors =>
        _descriptors.Values
            .OrderBy(d => d.Section, StringComparer.Ordinal)
            .ThenBy(d => d.Order)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>按分组取描述符，供设置页逐卡片渲染。</summary>
    public IReadOnlyList<IGrouping<string, SettingDescriptor>> Sections =>
        Descriptors.GroupBy(d => d.Section).ToList();

    /// <summary>注册一个设置项。键重复或描述符不自洽都会立刻抛错（把问题挡在启动期）。</summary>
    public void Register(SettingDescriptor descriptor)
    {
        descriptor.Validate();
        if (_descriptors.ContainsKey(descriptor.Key))
            throw new ArgumentException($"设置键重复注册：{descriptor.Key}");
        _descriptors[descriptor.Key] = descriptor;
    }

    public void RegisterRange(IEnumerable<SettingDescriptor> descriptors)
    {
        foreach (var d in descriptors) Register(d);
    }

    /// <summary>注册一条迁移：从旧结构的 JSON 变成新结构。</summary>
    public void RegisterMigration(Func<JsonObject, JsonObject> migration) => _migrations.Add(migration);

    public bool Has(string key) => _descriptors.ContainsKey(key);

    public SettingDescriptor Descriptor(string key) =>
        _descriptors.TryGetValue(key, out var d)
            ? d
            : throw new KeyNotFoundException($"未注册的设置项：{key}");

    // ---------------- 读写 ----------------

    /// <summary>取当前值；没有显式设置过则返回默认值。</summary>
    public T Get<T>(string key)
    {
        var d = Descriptor(key);
        object? current = null;
        var has = false;

        if (_values.TryGetValue(key, out var raw) && raw is not null)
        {
            // 枚举落盘是整数，读回来要还原成枚举
            if (d.Kind == SettingKind.Enum && raw is int i && d.EnumType is not null)
            {
                current = Enum.ToObject(d.EnumType, i);
                has = true;
            }
            else if (raw is T typed) { current = typed; has = true; }
            else
            {
                try { current = (T)Convert.ChangeType(raw, typeof(T))!; has = true; }
                catch (Exception) { /* 类型对不上时退回默认值，而不是抛给界面 */ }
            }
        }

        if (!has) current = d.DefaultValue ?? default(T);

        // 读取类事件：预览观察者可以替换读到的值。
        if (_events is not null)
        {
            var args = new ConfigEventArgs(key, ConfigEvent.Get, null, current, current);
            _events.RaiseRead(key, args);
            if (args.NewValueReplacement is not null) current = args.NewValueReplacement;
        }

        return (T)current!;
    }

    /// <summary>写值。会做类型与范围校验，非法值直接抛错（不要静默夹住，那会掩盖界面 bug）。</summary>
    public void Set<T>(string key, T value)
    {
        var d = Descriptor(key);
        var normalized = Normalize(d, value);
        var old = Get<object?>(key);

        // 更新类事件：预览观察者可以改写要写的值，或直接否决这次写入。
        //
        // 顺序与 PCL 一致：**先派发预览事件，再判断"值是否真的变了"**。
        // 否则「写一个与当前相同的值」会在事件派发之前就被短路掉 ——
        // 观察者永远收不到通知，而它恰恰是想在"有人试图写这个键"时被叫醒。
        if (_events is not null)
        {
            var args = new ConfigEventArgs(key, ConfigEvent.Set, null, old, normalized);
            var cancelled = _events.RaisePreview(key, args);
            if (cancelled) return;
            if (args.NewValueReplacement is not null) normalized = Normalize(d, args.NewValueReplacement);
        }

        if (Equals(old, normalized)) return;   // 值没变就不触发通知，避免界面白刷一遍

        _values[key] = normalized;
        Changed?.Invoke(key);

        _events?.Raise(key, new ConfigEventArgs(key, ConfigEvent.Set, null, old, normalized));
    }

    /// <summary>该键是否已偏离默认值。</summary>
    public bool IsChanged(string key)
    {
        if (!_values.TryGetValue(key, out var raw)) return false;
        var d = Descriptor(key);
        if (d.Kind == SettingKind.Enum && raw is int i && d.EnumType is not null)
            raw = Enum.ToObject(d.EnumType, i);
        return !Equals(raw, d.DefaultValue);
    }

    /// <summary>某个分组里是否有任何一项偏离默认值（对应 PCL 的 IsDefault 概念）。</summary>
    public bool IsSectionDefault(string section) =>
        Descriptors.Where(d => d.Section == section).All(d => !IsChanged(d.Key));

    /// <summary>重置单项到默认值。</summary>
    public void Reset(string key)
    {
        var d = Descriptor(key);
        var had = _values.ContainsKey(key);
        var old = Get<object?>(key);

        // 先派发预览事件，再动值 —— 否则被否决时值已经被删掉了。
        if (_events is not null)
        {
            var args = new ConfigEventArgs(key, ConfigEvent.Reset, null, old, d.DefaultValue);
            var cancelled = _events.RaisePreview(key, args);
            if (cancelled) return;   // 值原封不动
        }

        _values.Remove(key);
        if (had) Changed?.Invoke(key);

        _events?.Raise(key, new ConfigEventArgs(key, ConfigEvent.Reset, null, old, d.DefaultValue));
    }

    /// <summary>重置一个分组。</summary>
    public void ResetSection(string section)
    {
        foreach (var d in Descriptors.Where(d => d.Section == section).ToList()) Reset(d.Key);
    }

    /// <summary>重置全部设置。</summary>
    public void ResetAll()
    {
        foreach (var key in _values.Keys.ToList()) _values.Remove(key);
        foreach (var d in Descriptors) Changed?.Invoke(d.Key);
    }

    private object? Normalize(SettingDescriptor d, object? value)
    {
        switch (d.Kind)
        {
            case SettingKind.Bool:
                if (value is not bool b) throw Bad(d, value);
                return b;

            case SettingKind.Int:
            {
                if (value is not int n)
                {
                    if (value is null || !int.TryParse(value.ToString(), out n)) throw Bad(d, value);
                }
                CheckRange(d, n);
                return n;
            }

            case SettingKind.Double:
            {
                if (value is not double dv)
                {
                    if (value is null || !double.TryParse(value.ToString(), out dv)) throw Bad(d, value);
                }
                CheckRange(d, dv);
                return dv;
            }

            case SettingKind.String:
                if (value is not string s) throw Bad(d, value);
                return s;

            case SettingKind.Enum:
            {
                if (value is null) throw Bad(d, value);
                var t = d.EnumType!;
                if (!t.IsInstanceOfType(value))
                {
                    var text = value.ToString();
                    if (text is null || !Enum.TryParse(t, text, ignoreCase: true, out var parsed)) throw Bad(d, value);
                    value = parsed;
                }
                if (!Enum.IsDefined(t, value!)) throw Bad(d, value);
                return Convert.ToInt32(value);   // 落盘用整数，读回时还原
            }

            default:
                throw Bad(d, value);
        }
    }

    private static void CheckRange(SettingDescriptor d, double v)
    {
        if (d.Min is { } min && v < min)
            throw new ArgumentOutOfRangeException(d.Key, $"{d.Key} 的值 {v} 小于下限 {min}");
        if (d.Max is { } max && v > max)
            throw new ArgumentOutOfRangeException(d.Key, $"{d.Key} 的值 {v} 超过上限 {max}");
    }

    private static ArgumentException Bad(SettingDescriptor d, object? value) =>
        new($"{d.Key} 期望 {d.Kind}，实际收到 {value?.GetType().Name ?? "null"}");

    // ---------------- 落盘 ----------------

    /// <summary>把当前全部偏离默认值的项写入存储（默认值不落盘，保持文件精简）。</summary>
    public void Save()
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VersionKey] = SchemaVersion
        };

        foreach (var d in Descriptors)
        {
            if (!_values.TryGetValue(d.Key, out var raw)) continue;
            payload[d.Key] = raw;
        }

        _store.Save(payload);
    }

    /// <summary>从存储载入。会先跑迁移，再按描述符做类型还原；任何异常都不应拦住应用启动。</summary>
    public void Load()
    {
        IReadOnlyDictionary<string, object?> loaded;
        try { loaded = _store.Load(); }
        catch (Exception) { return; }   // 存储不可用：用默认值继续

        _values.Clear();
        foreach (var (key, value) in loaded)
        {
            if (key == VersionKey) continue;
            if (!_descriptors.TryGetValue(key, out var d)) continue;   // 未知键（来自旧版本或别的程序）忽略
            try { _values[key] = Normalize(d, value); }
            catch (Exception) { /* 单项非法就丢弃该项，不让整份配置失效 */ }
        }
    }

    /// <summary>
    /// 对一份原始 JSON 跑迁移。返回值是迁移后的对象。
    /// 单独抽出来是为了可测：迁移逻辑最容易写错，而写错会让用户"设置全丢"。
    /// </summary>
    public JsonObject Migrate(JsonObject raw)
    {
        var version = raw[VersionKey] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 1;
        var current = raw;

        // 未来新增不兼容变更时，在这里按 version 逐级补齐迁移函数
        foreach (var migration in _migrations)
        {
            if (version >= SchemaVersion) break;
            current = migration(current);
            version++;
        }

        current[VersionKey] = SchemaVersion;
        return current;
    }

    /// <summary>导出为 JSON（诊断页展示 / 备份用）。</summary>
    public string ToJson()
    {
        var obj = new JsonObject { [VersionKey] = SchemaVersion };
        foreach (var d in Descriptors)
        {
            if (!_values.TryGetValue(d.Key, out var raw)) continue;
            obj[d.Key] = JsonValue.Create(raw);
        }
        // 同样需要带 TypeInfoResolver 的 JsonSerializerOptions（见 JsonFileSettingsStore 的说明）
        return obj.ToJsonString(IndentedOptions);
    }
}
