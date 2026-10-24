namespace UMango.Core.Configuration;

/// <summary>
/// 配置观察者 → 实际应用动作 的登记表。
///
/// <para><b>它对应 PCL-CE 的什么</b>：PCL 的 <c>Plain Craft Launcher 2/Modules/Base/ModSetup.cs</c>
/// （686 行，**受限许可目录** → 只借鉴设计语义、独立重写）里，
/// 构造器给 30 个配置项各注册一个 <c>ConfigObserver(ConfigEvent.Changed, …)</c>，
/// 观察者回调里直接调用一个 <c>public static void XxxXxx(值)</c> 的应用方法；
/// 另有 <c>ApplyAll()</c> 在启动时把所有当前值主动应用一遍。</para>
///
/// <para><b>UMango 与 PCL 的关键差异</b>：</para>
/// <list type="bullet">
///   <item>PCL 的应用方法直接改 WPF 全局状态，**不可测**；
///         UMango 把"该改什么"抽成登记表 + 委托，**可以单测**：
///         改了什么、顺序如何、有没有漏项，都能断言。</item>
///   <item>PCL 用静态方法 + 静态字段；UMango 用实例，**没有全局静态状态**，
///         多个实例可以并存（测试里各测各的）。</item>
///   <item>PCL 的 <c>ApplyAll</c> 手工列举每个项；UMango 从登记表里
///         <b>按注册顺序</b>自动枚举 —— 少写一项不会静默漏掉。</item>
/// </list>
///
/// <para><b>用法</b>：</para>
/// <code>
/// var applier = new SetupApplier();
/// applier.Register("ui.appearance.windowOpacity", v =&gt; window.Opacity = (double)v);
/// applier.ApplyAll();                 // 启动时：按注册顺序应用所有当前值
/// service.Changed += key =&gt; applier.OnChanged(key);   // 运行中：值变了就重应用
/// </code>
/// </summary>
public sealed class SetupApplier
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    /// <summary>一条登记：某个配置键改变时要做什么。</summary>
    private sealed record Entry(string Key, Action<object?> Apply);

    /// <summary>已登记的键（按注册顺序）。</summary>
    public IReadOnlyList<string> Keys => _entries.Select(e => e.Key).ToList();

    /// <summary>已登记的条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 登记一条应用动作。
    /// </summary>
    /// <param name="key">配置键。</param>
    /// <param name="apply">应用动作。参数是读回来的**当前值**。</param>
    /// <exception cref="ArgumentException">键为空或重复登记。</exception>
    public void Register(string key, Action<object?> apply)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("配置键不能为空", nameof(key));
        ArgumentNullException.ThrowIfNull(apply);
        if (_byKey.ContainsKey(key)) throw new ArgumentException($"配置键重复登记：{key}", nameof(key));

        var entry = new Entry(key, apply);
        _entries.Add(entry);
        _byKey[key] = entry;
    }

    /// <summary>某键是否已登记。</summary>
    public bool Contains(string key) => key is not null && _byKey.ContainsKey(key);

    /// <summary>
    /// 应用某个键对应的动作。
    /// </summary>
    /// <returns>该键已登记且执行成功返回 true；未登记返回 false（不抛错 —— 未登记是正常情况）。</returns>
    public bool Apply(string key, object? value)
    {
        if (key is null || !_byKey.TryGetValue(key, out var entry)) return false;
        entry.Apply(value);
        return true;
    }

    /// <summary>
    /// 某个键变化时调用：读出当前值并应用。
    ///
    /// <para>动作抛异常时**不吞掉** —— 设置页改了某个选项却静默无效，
    /// 是最难查的一类 bug。调用方（App 层）决定要不要兜底。</para>
    /// </summary>
    /// <param name="key">变化的键。</param>
    /// <param name="readValue">读当前值的委托（通常是 <c>service.Get&lt;T&gt;</c>）。</param>
    /// <returns>已登记并应用成功返回 true。</returns>
    public bool OnChanged(string key, Func<string, object?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        if (key is null || !_byKey.ContainsKey(key)) return false;
        return Apply(key, readValue(key));
    }

    /// <summary>
    /// 按**注册顺序**应用所有已登记的键。
    ///
    /// <para>顺序有意义：比如「窗口不透明度」要在「窗口主题」之后应用、
    /// 「模糊半径」要在「模糊开关」之后应用。PCL 的 <c>ApplyAll</c> 靠手写顺序保证这一点，
    /// UMango 靠登记顺序 —— 登记时把顺序排对即可。</para>
    ///
    /// <param name="readValue">读当前值的委托。</param>
    /// <returns>实际应用了几个。</returns>
    public int ApplyAll(Func<string, object?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        var applied = 0;
        foreach (var entry in _entries)
        {
            entry.Apply(readValue(entry.Key));
            applied++;
        }
        return applied;
    }
}
