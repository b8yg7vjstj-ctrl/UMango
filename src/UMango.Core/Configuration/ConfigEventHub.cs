namespace UMango.Core.Configuration;

/// <summary>
/// 配置作用域：一组配置键的集合。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/IConfigScope.cs</c>（Apache-2.0）。
/// PCL 的实现由源生成器按 <c>[ConfigGroup]</c> 特性生成；UMango 用普通类表达同样的语义 ——
/// 于是作用域可以由描述符动态拼出来（比如"所有 <c>ui.*</c> 开头的键"），不需要编译期魔法。
/// </summary>
public interface IConfigScope
{
    /// <summary>本作用域的名字（用于日志与界面提示）。</summary>
    string Name { get; }

    /// <summary>本作用域包含哪些键。</summary>
    IReadOnlyList<string> Keys { get; }

    /// <summary>检查给定键里哪些落在本作用域内。</summary>
    IEnumerable<string> CheckScope(IReadOnlySet<string> keys);
}

/// <summary>
/// 静态作用域：由一组明确的键构成。
/// </summary>
public sealed class ConfigScope : IConfigScope
{
    private readonly HashSet<string> _keys;

    /// <summary>构造。</summary>
    /// <param name="name">作用域名。</param>
    /// <param name="keys">包含的键。</param>
    public ConfigScope(string name, IEnumerable<string> keys)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("作用域名不能为空", nameof(name));
        Name = name;
        _keys = new HashSet<string>(keys, StringComparer.Ordinal);
        Keys = _keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Keys { get; }

    /// <inheritdoc />
    public IEnumerable<string> CheckScope(IReadOnlySet<string> keys) => keys.Where(_keys.Contains);

    /// <summary>
    /// 按前缀构造作用域，例如所有 <c>ui.</c> 开头的键。
    /// </summary>
    public static ConfigScope ByPrefix(string name, IEnumerable<string> allKeys, string prefix) =>
        new(name, allKeys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)));
}

/// <summary>
/// 配置事件总线：把观察者挂到具体键上，并按 PCL-CE 的"预览 / 普通"两段语义派发事件。
///
/// **为什么不直接把事件塞进 <see cref="SettingsService"/>**：
/// 设置服务负责"值怎么存"，事件总线负责"谁在什么时候被通知"，
/// 两件事分开之后，事件派发的顺序与替换规则可以单独被单测覆盖，
/// 而不必牵动整个设置服务。
/// </summary>
public sealed class ConfigEventHub
{
    private readonly Dictionary<string, List<ConfigObserver>> _observers = new(StringComparer.Ordinal);

    /// <summary>订阅某个键的事件。</summary>
    public void Observe(string key, ConfigObserver observer)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("配置键不能为空", nameof(key));
        if (!_observers.TryGetValue(key, out var list))
        {
            list = [];
            _observers[key] = list;
        }
        if (!list.Contains(observer)) list.Add(observer);
    }

    /// <summary>退订某个键的事件。</summary>
    public bool Unobserve(string key, ConfigObserver observer) =>
        _observers.TryGetValue(key, out var list) && list.Remove(observer);

    /// <summary>某键当前挂了几个观察者（测试与诊断用）。</summary>
    public int ObserverCount(string key) => _observers.TryGetValue(key, out var list) ? list.Count : 0;

    /// <summary>清空某键的全部观察者。</summary>
    public void Clear(string key) => _observers.Remove(key);

    /// <summary>清空全部观察者。</summary>
    public void ClearAll() => _observers.Clear();

    /// <summary>
    /// 派发一次**预览**事件（写入/重置之前）。
    /// </summary>
    /// <returns>被否决时返回 <c>true</c>。</returns>
    public bool RaisePreview(string key, ConfigEventArgs args) => _Dispatch(key, args, previewOnly: true);

    /// <summary>
    /// 派发一次**普通**事件（写入/重置之后）。
    /// 预览阶段定下的替换值与否决状态在这里**不会**再次传播 ——
    /// 普通观察者只做"事后通知"。
    /// </summary>
    public void Raise(string key, ConfigEventArgs args) => _Dispatch(key, args, previewOnly: false);

    /// <summary>
    /// 派发读取类事件（Get / CheckDefault）。
    /// 这里没有"预览 / 普通"之分：读取时两类观察者都会跑，
    /// 但只有**预览**观察者的替换值会被采纳。
    /// </summary>
    public void RaiseRead(string key, ConfigEventArgs args)
    {
        var list = _observers.TryGetValue(key, out var l) ? l : [];
        var replaced = false;

        foreach (var observer in list.Where(o => (o.Event & args.Event) != 0))
        {
            observer.Handler(args);
            if (observer.IsPreview)
            {
                if (args.NewValueReplacement is not null) replaced = true;
                if (args.Cancelled) return;
            }
            else if (!replaced && args.NewValueReplacement is not null)
            {
                // 普通观察者不得把替换值传播下去
                args.NewValueReplacement = null;
            }
        }
    }

    private bool _Dispatch(string key, ConfigEventArgs args, bool previewOnly)
    {
        var list = _observers.TryGetValue(key, out var l) ? l : [];
        var replaced = false;

        foreach (var observer in list.Where(o => (o.Event & args.Event) != 0))
        {
            if (observer.IsPreview != previewOnly) continue;

            observer.Handler(args);

            if (observer.IsPreview)
            {
                if (args.NewValueReplacement is not null) replaced = true;
                if (args.Cancelled) return true;
            }
            else if (!replaced && args.NewValueReplacement is not null)
            {
                args.NewValueReplacement = null;
            }
        }

        return false;
    }
}
