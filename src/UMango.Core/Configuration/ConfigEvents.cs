namespace UMango.Core.Configuration;

/// <summary>
/// 配置项事件类型（位标志）。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/ConfigEvent.cs</c>（Apache-2.0），
/// 语义完全一致：一个观察者可以订阅若干类事件，用位与判断是否命中。
/// </summary>
[Flags]
public enum ConfigEvent
{
    /// <summary>保留（无事件）。</summary>
    None = 0,

    /// <summary>初始化：程序启动、配置项首次注册时触发一次。</summary>
    Init = 0b00001,

    /// <summary>读取配置值时触发。</summary>
    Get = 0b00010,

    /// <summary>设置配置值时触发。</summary>
    Set = 0b00100,

    /// <summary>重置配置值（回到默认值）时触发。</summary>
    Reset = 0b01000,

    /// <summary>检查"是否等于默认值"时触发。</summary>
    CheckDefault = 0b10000,

    /// <summary>所有读取类事件（<see cref="Get"/> | <see cref="CheckDefault"/>）。</summary>
    Read = Get | CheckDefault,

    /// <summary>所有更新类事件（<see cref="Set"/> | <see cref="Reset"/>）。</summary>
    Update = Set | Reset,

    /// <summary>所有改变类事件（初始化 + 更新）。</summary>
    Changed = Init | Update,

    /// <summary>所有事件。别监听它 —— 一点风吹草动都会触发。</summary>
    All = Read | Changed
}

/// <summary>配置事件处理委托。</summary>
/// <param name="e">事件参数。</param>
public delegate void ConfigEventHandler(ConfigEventArgs e);

/// <summary>
/// 配置事件观察器。
///
/// <para><b>预览（IsPreview）语义</b>是这套机制的关键：</para>
/// <list type="bullet">
///   <item><b>预览观察者</b>先跑。它们可以<b>改写</b>要写入的值（<see cref="ConfigEventArgs.NewValueReplacement"/>），
///         也可以<b>否决</b>这次操作（<see cref="ConfigEventArgs.Cancelled"/>）。</item>
///   <item><b>普通观察者</b>后跑。它们<b>不能</b>否决操作，也<b>不能</b>改写值 ——
///         否则"监听者"就变成了"隐式中间件"，改写顺序会变得不可预测。</item>
/// </list>
///
/// 这与 PCL-CE 的行为一致（PCL 在 <c>ConfigItem._TriggerEvent</c> 里用
/// <c>replaceNewValue</c> 标志区分两段）。
/// </summary>
/// <param name="Event">关心的事件类型。</param>
/// <param name="Handler">处理委托。</param>
/// <param name="IsPreview">是否为预览观察者。</param>
public sealed record ConfigObserver(ConfigEvent Event, ConfigEventHandler Handler, bool IsPreview = false);

/// <summary>
/// 配置事件参数。
///
/// 用普通类而不是 record：<see cref="NewValueReplacement"/> 与 <see cref="Cancelled"/>
/// 本身就是"让观察者修改"的通道，用不可变 record 的话每次改都要重新分配。
/// </summary>
public sealed class ConfigEventArgs
{
    /// <summary>构造。</summary>
    /// <param name="key">配置键。</param>
    /// <param name="event">触发的事件类型。</param>
    /// <param name="argument">上下文参数。</param>
    /// <param name="oldValue">旧值。</param>
    /// <param name="newValue">新值。</param>
    public ConfigEventArgs(string key, ConfigEvent @event, object? argument = null,
        object? oldValue = null, object? newValue = null)
    {
        Key = key;
        Event = @event;
        Argument = argument;
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>配置键。</summary>
    public string Key { get; }

    /// <summary>触发的事件类型。</summary>
    public ConfigEvent Event { get; }

    /// <summary>上下文参数（用于区分同一键在不同作用域下的取值）。</summary>
    public object? Argument { get; }

    /// <summary>旧值（可能为 null —— 之前没有显式设置过）。</summary>
    public object? OldValue { get; }

    /// <summary>新值。</summary>
    public object? NewValue { get; }

    /// <summary>
    /// 替代新值。只有**预览观察者**写它才有意义；
    /// 写了之后本次操作实际写入的就是这个值。
    /// </summary>
    public object? NewValueReplacement { get; set; }

    /// <summary>
    /// 否决本次操作。只有**预览观察者**写它才有意义。
    /// 写它之后本次操作直接放弃（不写盘、不触发后续通知）。
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>实际生效的值（被替换过就用替换值）。</summary>
    public object? Value => NewValueReplacement ?? NewValue;

    /// <summary>当前值（未设置时为 null）。</summary>
    public object? Current => OldValue;
}
