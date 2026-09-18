namespace UMango.Core.Configuration;

/// <summary>内存实现：供单测使用，也让"不落盘"成为可选项。</summary>
public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, object?> _values = [];

    public int SaveCount { get; private set; }

    public IReadOnlyDictionary<string, object?> Load() => new Dictionary<string, object?>(_values);

    public void Save(IReadOnlyDictionary<string, object?> values)
    {
        SaveCount++;
        _values.Clear();
        foreach (var (k, v) in values) _values[k] = v;
    }
}
