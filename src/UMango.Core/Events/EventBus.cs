namespace UMango.Core.Events;

/// <summary>
/// 轻量级事件总线：发布/订阅模式，用于解耦组件。
///
/// 对应 PCL-CE 的 Modules/Event/CustomEventService.cs（Apache-2.0，部分）。
/// UMango 当前用 MVVM 属性通知，但某些跨页面的"全局事件"（如"搜索结果已更新"、
/// "设置已更改"）用属性通知不够——它们需要从 ViewModel 发出、由另一个 ViewModel 收到。
/// 事件总线正是为此而生。
///
/// 注意：这不替代 MVVM 的 INotifyPropertyChanged（那是属性级通知），
/// 只用于跨 ViewModel 的广播式通知。
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
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

    private sealed class Unsubscriber(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
