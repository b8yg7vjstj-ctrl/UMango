using System.Diagnostics.CodeAnalysis;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// 动态缓存存取仓库：按"上下文对象"分组，每组一个独立的底层存取仓库。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/DynamicCacheConfigStorage.cs</c>（Apache-2.0）。
///
/// **与 PCL 的实质差异**：PCL 的 <see cref="InvalidateCache"/> 从字典里移除上下文时
/// **忘记调用 <c>Stop()</c>** —— 那个底层存储可能还挂着文件句柄 / 定时器，
/// 缓存"失效"了但资源没释放。UMango 改成：失效时先 <c>Stop()</c> 再移除。
/// </summary>
public sealed class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private ConfigStorage? _nullContextCache;

    /// <summary>存取仓库工厂：没有匹配的上下文实例时用它创建新的。</summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    /// <summary>已建立的上下文数量（不含 null 上下文）。</summary>
    public int CachedContextCount => _cache.Count;

    /// <inheritdoc />
    protected override bool OnAccess<TKey, TValue>(
        StorageAction action,
        ref TKey key,
        [NotNullWhen(true)] ref TValue value,
        object? context)
    {
        ConfigStorage? storage;
        if (context is null) storage = _nullContextCache;
        else _cache.TryGetValue(context, out storage);

        if (storage is null)
        {
            try
            {
                storage = StorageFactory(context);
                if (context is null) _nullContextCache = storage;
                else _cache[context] = storage;
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to invoke storage factory", ex);
            }
        }

        return storage.Access(action, ref key, ref value, context);
    }

    /// <inheritdoc />
    public override void Stop()
    {
        foreach (var item in _cache.Values) item.Stop();
        _cache.Clear();
        _nullContextCache?.Stop();
        _nullContextCache = null;
    }

    /// <summary>让某个上下文的缓存失效（并释放它的底层存储）。</summary>
    public bool InvalidateCache(object context)
    {
        if (!_cache.TryGetValue(context, out var center)) return false;
        center.Stop();
        _cache.Remove(context);
        return true;
    }
}

/// <summary>
/// 可枚举键的键值文件。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/IEnumerableKeyProvider.cs</c>（Apache-2.0）。
/// </summary>
public interface IEnumerableKeyProvider
{
    /// <summary>文件包含的所有键（通常是耗时操作，慎用）。</summary>
    IEnumerable<string> Keys { get; }
}
