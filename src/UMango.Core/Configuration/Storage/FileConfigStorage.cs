using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace UMango.Core.Configuration.Storage;

/// <summary>
/// 文件存取仓库：把键值操作转发给一个 <see cref="IKeyValueFileProvider"/>。
///
/// 取自 PCL-CE 的 <c>PCL.Core/App/Configuration/Storage/FileConfigStorage.cs</c>（Apache-2.0）。
///
/// **与 PCL 的三处实质差异**：
/// <list type="number">
///   <item>PCL 的写入走 <c>Channel</c> + 10 秒合并落盘：写入先**排队**，
///         真正的 <c>File.Set</c> 发生在 10 秒后的那次同步里。
///         后果是进程被强杀/崩溃时，最后一批写入**直接丢**（队列在内存里）。
///         UMango 改成：<b>内存里的值立即生效</b>（<c>File.Set</c> 立刻调用），
///         磁盘落盘用 250ms 防抖合并，并提供 <see cref="FlushAsync"/> 让测试与关机流程
///         能确定性地等它落完。</item>
///   <item>PCL 读失败时会把损坏的键从文件里删掉并返回 false（相当于重置为默认值）。
///         UMango 保留这个行为（否则一个坏值会永久卡住该键），但<b>额外触发
///         <see cref="CorruptedKeyRemoved"/></b> 事件把键名报出来，而不是静默。</item>
///   <item>PCL 的 <c>Stop()</c> 依赖一个 <c>ManualResetEventSlim</c> 等后台任务收尾；
///         UMango 改成同步落盘 —— 关机路径不需要额外的线程与事件。</item>
/// </list>
/// </summary>
public sealed class FileConfigStorage : ConfigStorage, IEnumerableKeyProvider
{
    // OnAccess 的契约是「返回 true 表示 value 有效」，但 TValue 是无约束泛型 ——
    // 编译器无法证明 Get 分支赋进去的非空值对 bool/引用类型都成立。
    // PCL 原版在同一处用了同样的 pragma；这里保留它，不放宽契约本身。
#pragma warning disable CS8762
    private readonly IKeyValueFileProvider _file;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>构造。</summary>
    public FileConfigStorage(IKeyValueFileProvider file) => _file = file;

    /// <summary>键值文件。</summary>
    public IKeyValueFileProvider File => _file;

    /// <summary>
    /// 全部键。只有底层文件支持枚举时才有值 —— 否则返回空集合。
    /// </summary>
    public IEnumerable<string> Keys =>
        _file is IEnumerableKeyProvider enumerable ? enumerable.Keys : [];

    /// <summary>读取失败（值已损坏、被移除）时触发，参数为键名。</summary>
    public event Action<string>? CorruptedKeyRemoved;

    /// <inheritdoc />
    protected override bool OnAccess<TKey, TValue>(
        StorageAction action,
        ref TKey key,
        [NotNullWhen(true)] ref TValue value,
        object? argument)
    {
        if (key is not string strKey) throw new NotSupportedException($"Key '{key}' is not supported");

        switch (action)
        {
            case StorageAction.Get:
            {
                if (!_file.Exists(strKey)) return false;
                try
                {
                    value = _file.Get<TValue>(strKey);
                    return true;
                }
                catch (Exception ex) when (ex is JsonException
                                           or InvalidCastException
                                           or FormatException
                                           or OverflowException
                                           or ArgumentException
                                           or InvalidDataException)
                {
                    // 坏值不能永远卡住这个键：删掉它，让它回到默认值。
                    _file.Remove(strKey);
                    ScheduleSync();
                    CorruptedKeyRemoved?.Invoke(strKey);
                    return false;
                }
            }

            case StorageAction.Exists:
            {
                if (typeof(TValue) == typeof(bool))
                {
                    var exists = _file.Exists(strKey);
                    value = Unsafe.As<bool, TValue>(ref exists);
                    return true;
                }
                throw new InvalidOperationException($"Storage action '{StorageAction.Exists}' must have a boolean value");
            }

            case StorageAction.Set:
            {
                var localValue = value;
                _file.Set(strKey, localValue);
                ScheduleSync();
                return false;
            }

            case StorageAction.Delete:
            {
                _file.Remove(strKey);
                ScheduleSync();
                return false;
            }

            default: throw new InvalidOperationException($"Invalid storage action: {action}");
        }
    }

    private void ScheduleSync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => SafeSync(), null, Timeout.Infinite, Timeout.Infinite);
            // 合并落盘：250ms 内的多次写只落一次盘。
            _timer.Change(250, Timeout.Infinite);
        }
    }

    private void SafeSync()
    {
        try
        {
            _file.Sync();
        }
        catch (Exception)
        {
            // 防抖定时器里的异常没人接 —— 吞掉，让下一次写再试。
        }
    }

    /// <summary>立即落盘。</summary>
    public void Flush() => _file.Sync();

    /// <summary>立即落盘（异步形态，便于调用方 await）。</summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _file.Sync();
        return Task.CompletedTask;
    }

#pragma warning restore CS8762

    /// <inheritdoc />
    public override void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        try
        {
            _file.Sync();
        }
        catch (Exception)
        {
            // 关机路径上的落盘失败没有再向上的通道 —— 记在这里就够了。
        }
    }
}
