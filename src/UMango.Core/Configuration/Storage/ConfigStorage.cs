using System.Diagnostics.CodeAnalysis;

namespace UMango.Core.Configuration;

/// <summary>
/// 存取操作类型。
/// </summary>
public enum StorageAction
{
    Get,
    Exists,
    Set,
    Delete
}

/// <summary>
/// 配置提供方：按键读写。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/IConfigProvider.cs</c>（Apache-2.0），接口形状一致。
/// </summary>
public interface IConfigProvider
{
    /// <summary>取值。键不存在时返回 false。</summary>
    bool GetValue<T>(string key, [NotNullWhen(true)] out T? value, object? argument = null);

    /// <summary>写值。</summary>
    void SetValue<T>(string key, T value, object? argument = null);

    /// <summary>删键。</summary>
    void Delete(string key, object? argument = null);

    /// <summary>键是否存在。</summary>
    bool Exists(string key, object? argument = null);
}

/// <summary>
/// 键值文件模型：一个"能存若干键值、能把它们落盘"的文件。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/IKeyValueFileProvider.cs</c>（Apache-2.0）。
/// </summary>
public interface IKeyValueFileProvider
{
    /// <summary>文件路径。</summary>
    string FilePath { get; }

    /// <summary>取值。键不存在时抛 <see cref="KeyNotFoundException"/>。</summary>
    T Get<T>(string key);

    /// <summary>写值。</summary>
    void Set<T>(string key, T value);

    /// <summary>键是否存在。</summary>
    bool Exists(string key);

    /// <summary>删键。</summary>
    void Remove(string key);

    /// <summary>把当前内容落盘。</summary>
    void Sync();
}

/// <summary>
/// 存取仓库的基类：把"怎么存"与"存什么格式"分开。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/ConfigStorage.cs</c>（Apache-2.0）。
///
/// **UMango 与 PCL 的重要差异**：PCL 的 <c>Access</c> 捕获任何异常后调用
/// <c>Lifecycle.ForceShutdown(-2)</c> —— 配置读坏了就让整个程序退出。
/// UMango 改成**抛给调用方**：一次设置读写失败不该让用户丢掉整个会话。
/// </summary>
public abstract class ConfigStorage : IConfigProvider
{
    /// <summary>子类实现：真正干活的存取动作。</summary>
    protected abstract bool OnAccess<TKey, TValue>(
        StorageAction action,
        ref TKey key,
        [NotNullWhen(true)] ref TValue value,
        object? argument);

    /// <summary>停止存取工作，保存并释放资源。</summary>
    public virtual void Stop() { }

    /// <summary>执行一次存取操作。</summary>
    public bool Access<TKey, TValue>(
        StorageAction action,
        ref TKey key,
        [NotNullWhen(true)] ref TValue value,
        object? argument) => OnAccess(action, ref key, ref value, argument);

    /// <inheritdoc />
    public bool GetValue<T>(string key, [NotNullWhen(true)] out T? value, object? argument = null)
    {
        var keyRef = key;
        T? valueRef = default;
        var hasValue = Access(StorageAction.Get, ref keyRef, ref valueRef, argument);
        value = valueRef;
        return hasValue;
    }

    /// <inheritdoc />
    public void SetValue<T>(string key, T value, object? argument = null)
    {
        var keyRef = key;
        var valueRef = value;
        Access(StorageAction.Set, ref keyRef, ref valueRef, argument);
    }

    /// <inheritdoc />
    public void Delete(string key, object? argument = null)
    {
        var keyRef = key;
        object? valueRef = null;
        Access(StorageAction.Delete, ref keyRef, ref valueRef, argument);
    }

    /// <inheritdoc />
    public bool Exists(string key, object? argument = null)
    {
        var keyRef = key;
        var resultRef = false;
        return Access(StorageAction.Exists, ref keyRef, ref resultRef, argument) && resultRef;
    }
}
