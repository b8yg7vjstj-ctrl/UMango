namespace UMango.Core.Logging;

/// <summary>
/// 日志输出目标。
///
/// **实现约束（硬性）**：<see cref="Write"/> **绝不允许抛异常**。
/// 日志是排查问题的最后一道手段 —— 如果它自己会因为磁盘满、权限不足、
/// 路径被占用而抛，那就变成了"为了记录失败而制造新的失败"。
/// 实现里必须自行 try/catch 并静默降级。
/// </summary>
public interface ILogSink
{
    void Write(LogEntry entry);

    /// <summary>把缓冲刷到持久介质。进程退出前调用。</summary>
    void Flush();
}
