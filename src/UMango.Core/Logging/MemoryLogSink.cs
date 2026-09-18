namespace UMango.Core.Logging;

/// <summary>
/// 内存环形缓冲：给界面"日志页"直接读，不必去解析文件。
///
/// 有容量上限是刻意的：CLI 的输出可能有几万行，
/// 无上限会让日志本身成为内存泄漏来源。
/// </summary>
public sealed class MemoryLogSink(int capacity = 2000) : ILogSink
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly Lock _gate = new();

    public int Capacity { get; } = Math.Max(50, capacity);

    /// <summary>当前缓冲里的条数</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>已达容量上限而被丢弃的条数（不静默丢：界面会提示）</summary>
    public long DroppedCount { get; private set; }

    /// <summary>新增日志时触发（界面订阅它实时刷新）</summary>
    public event Action<LogEntry>? Appended;

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
                DroppedCount++;
            }
        }

        // 在锁外触发，避免订阅者回调里再访问本对象造成死锁
        Appended?.Invoke(entry);
    }

    public void Flush() { /* 内存实现无需落盘 */ }

    /// <summary>快照（时间正序）。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}
