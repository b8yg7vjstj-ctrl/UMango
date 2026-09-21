namespace UMango.Core.Cache;

/// <summary>
/// 通用 LRU（最近最少使用）缓存。超过容量上限时淘汰最久未访问的条目。
/// 使用场景：HTTP 响应缓存、CLI 输出缓存、搜索结果缓存。
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _dict;
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();
    private readonly Lock _gate = new();
    public long Evictions { get; private set; }
    public int Count { get { lock (_gate) return _dict.Count; } }

    public LruCache(int capacity = 100)
    {
        _capacity = Math.Max(1, capacity);
        _dict = new(capacity);
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate) { if (_dict.TryGetValue(key, out var n)) { _list.Remove(n); _list.AddFirst(n); value = n.Value.Value; return true; } value = default; return false; }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_dict.TryGetValue(key, out var e)) { e.Value = (key, value); _list.Remove(e); _list.AddFirst(e); return; }
            if (_dict.Count >= _capacity) { var o = _list.Last!; _list.RemoveLast(); _dict.Remove(o.Value.Key); Evictions++; }
            var node = new LinkedListNode<(TKey, TValue)>((key, value)); _list.AddFirst(node); _dict[key] = node;
        }
    }

    public bool TryRemove(TKey key) { lock (_gate) { if (_dict.Remove(key, out var n)) { _list.Remove(n); return true; } return false; } }
    public void Clear() { lock (_gate) { _dict.Clear(); _list.Clear(); } }
}
