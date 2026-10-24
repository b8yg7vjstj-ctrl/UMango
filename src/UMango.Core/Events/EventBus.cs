using System.Collections.Concurrent;

namespace UMango.Core.Events;

/// <summary>
/// 轻量级事件总线：发布/订阅模式，用于解耦组件。
///
/// 对应 PCL-CE 的 PCL.Core/App/EventBus/（4 文件 290 行，Apache-2.0）。
/// PCL 那边是**静态**单例 + 通道名（channel）路由；UMango 保留**实例**语义
/// （静态总线会让测试之间互相污染，本仓库已有此类事故），
/// 但补齐 PCL 的通道能力：
///
///   · **通道（channel）**：同一实例内按名字隔离一组事件，
///     避免「所有地方都订阅同一个泛型类型」造成的隐式耦合。
///   · **异步发布**：处理器可以是 <see cref="Func{TEventData, Task}"/>。
///   · **可赋值性匹配**：订阅 <c>object</c> 的处理器能收到 <c>Foo</c> 事件
///     （PCL 用 <c>registeredType.IsAssignableFrom(eventType)</c>）。
///   · **弱引用持有者**：处理器对象被回收后自动退订，不阻止垃圾回收。
///
/// 注意：这不替代 MVVM 的 INotifyPropertyChanged（那是属性级通知），
/// 只用于跨 ViewModel 的广播式通知。
/// </summary>
public sealed class EventBus
{
    /// <summary>类型化订阅表（无通道）。</summary>
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    /// <summary>通道订阅表：通道名 → 事件类型 → 订阅项。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Type, ConcurrentDictionary<Guid, ChannelSubscription>>> _channels = new();

    private readonly Lock _lock = new();

    /// <summary>订阅一个事件类型。返回一个可释放的句柄。</summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        var type = typeof(T);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = [];
                _handlers[type] = list;
            }
            list.Add(handler);
        }
        return new Unsubscriber(() => { lock (_lock) { if (_handlers.TryGetValue(type, out var l)) l.Remove(handler); } });
    }

    /// <summary>发布一个事件。所有订阅者都会收到。</summary>
    public void Publish<T>(T evt) where T : class
    {
        List<Delegate> snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list) || list.Count == 0) return;
            snapshot = list.ToList();
        }
        foreach (var handler in snapshot)
        {
            try { ((Action<T>)handler)(evt); }
            catch { /* 单个订阅者异常不应阻止其它订阅者 */ }
        }
    }

    #region 通道

    /// <summary>显式创建通道。已存在时返回 false（不抛异常，便于幂等初始化）。</summary>
    public bool AddChannel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _channels.TryAdd(name, []);
    }

    /// <summary>移除通道及其全部订阅。通道不存在返回 false。</summary>
    public bool RemoveChannel(string name) => _channels.TryRemove(name, out _);

    /// <summary>当前已创建的通道名。</summary>
    public IReadOnlyCollection<string> Channels => [.. _channels.Keys];

    /// <summary>
    /// 某通道上的订阅总数（已回收持有者的条目在下次发布时会被顺手清掉，
    /// 因此这个数在两次发布之间可能偏大）。
    ///
    /// 暴露它是为了让「弱订阅被回收后不留下垃圾订阅」这件事**可被验证** ——
    /// 否则清理与否在行为上完全一样，只能靠读代码判断。
    /// </summary>
    public int SubscriberCount(string channel)
    {
        if (!_channels.TryGetValue(channel, out var bucket)) return 0;
        return bucket.Values.Sum(byType => byType.Count);
    }

    /// <summary>
    /// 在指定通道上订阅异步事件处理器。
    /// 通道不存在会**自动创建**（PCL 的行为），订阅时传 null 处理器抛 <see cref="ArgumentNullException"/>。
    /// </summary>
    public IDisposable Subscribe<TEventData>(string channel, Func<TEventData, Task> handler) where TEventData : class
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("通道名不能为空。", nameof(channel));
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        var entry = new ChannelSubscription(async evt => await handler((TEventData)evt).ConfigureAwait(false));
        var bucket = _channels.GetOrAdd(channel, _ => []);
        var byType = bucket.GetOrAdd(typeof(TEventData), _ => []);
        byType[id] = entry;

        return new Unsubscriber(() =>
        {
            if (bucket.TryGetValue(typeof(TEventData), out var types) && types.TryRemove(id, out _))
            {
                if (types.IsEmpty) bucket.TryRemove(typeof(TEventData), out _);
            }
        });
    }

    /// <summary>
    /// 在指定通道上订阅持有者对象（弱引用）。持有者被回收后自动退订。
    /// </summary>
    public IDisposable Subscribe<TEventData>(string channel, IEventHandler<TEventData> handler) where TEventData : class
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("通道名不能为空。", nameof(channel));
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        var ownerRef = new WeakReference<object>(handler);
        var entry = new ChannelSubscription(
            async evt =>
            {
                if (ownerRef.TryGetTarget(out var target) && target is IEventHandler<TEventData> typed)
                {
                    await typed.HandleEventAsync((TEventData)evt).ConfigureAwait(false);
                }
            },
            ownerRef);
        var bucket = _channels.GetOrAdd(channel, _ => []);
        var byType = bucket.GetOrAdd(typeof(TEventData), _ => []);
        byType[id] = entry;

        return new Unsubscriber(() =>
        {
            if (bucket.TryGetValue(typeof(TEventData), out var types) && types.TryRemove(id, out _))
            {
                if (types.IsEmpty) bucket.TryRemove(typeof(TEventData), out _);
            }
        });
    }

    /// <summary>
    /// 异步发布到指定通道。
    /// 通道不存在抛 <see cref="InvalidOperationException"/>（与 PCL 一致：频道写错应当被看见，而不是静默吞掉）。
    /// 单个处理器抛异常不影响其它处理器。
    /// </summary>
    public async Task PublishAsync<TEventData>(string channel, TEventData data) where TEventData : class
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("通道名不能为空。", nameof(channel));
        if (!_channels.TryGetValue(channel, out var bucket))
        {
            throw new InvalidOperationException($"未找到通道「{channel}」。");
        }

        var eventType = data.GetType();
        var matching = new List<Func<object, Task>>();
        foreach (var (registeredType, handlers) in bucket)
        {
            // 可赋值性匹配：订阅 object 能收到 Foo（PCL 的 IsAssignableFrom 语义）
            if (!registeredType.IsAssignableFrom(eventType)) continue;
            foreach (var (id, entry) in handlers.ToArray())
            {
                if (entry.IsOwnerCollected)
                {
                    // 持有者已被回收 —— 顺手清掉这条订阅
                    handlers.TryRemove(id, out _);
                    continue;
                }
                matching.Add(entry.Handler);
            }
        }

        var tasks = matching.Select(async h =>
        {
            try { await h(data).ConfigureAwait(false); }
            catch { /* 单个处理器异常不应阻止其它处理器 */ }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    #endregion

    private sealed class Unsubscriber(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    private sealed class ChannelSubscription(Func<object, Task> handler, WeakReference<object>? ownerRef = null)
    {
        public Func<object, Task> Handler { get; } = handler;

        /// <summary>持有者弱引用（仅弱订阅时非空）。</summary>
        public WeakReference<object>? OwnerRef { get; } = ownerRef;

        /// <summary>持有者是否已被回收（弱订阅专用）。</summary>
        public bool IsOwnerCollected => OwnerRef is not null && !OwnerRef.TryGetTarget(out _);
    }
}

/// <summary>事件持有者契约。实现类可被事件总线弱引用持有。</summary>
public interface IEventHandler<in TEventData> where TEventData : class
{
    /// <summary>处理一个被发布的事件。</summary>
    Task HandleEventAsync(TEventData eventData);
}
