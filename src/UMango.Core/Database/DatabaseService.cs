using System;
using System.Collections.Concurrent;
using System.IO;
using LiteDB;

namespace UMango.Core.Database;

/// <summary>
/// 数据库连接服务 —— 采用自 PCL-CE <c>PCL.Core/App/Database/DatabaseService.cs</c>。
///
/// **来源与许可**：原文件属于 PCL-CE 的 <c>PCL.Core/</c>（**Apache-2.0**，可放心复用）。
/// 原实现是 48 行的连接缓存，但依赖 PCL 自有的 IoC 生命周期体系
/// （<c>GeneralService</c> / <c>LifecycleServiceAttribute</c>，共 13 个文件），
/// 而 UMango 没有那套东西。照搬进来就得把整个 IoC 一起搬进来 ——
/// 为 48 行代码引入一个生命周期框架，代价与收益不成比例。
/// 故此处**只保留语义、重写实现**：连接按路径缓存、路径非法即抛、退出时全部释放。
///
/// **为什么不直接用 PCL 原版**：见上。差异仅在于生命周期托管方式 ——
/// PCL 靠 <c>LifecycleService</c> 的 <c>Stop()</c> 释放，UMango 靠 <see cref="Dispose"/>，
/// 两者时机一致（进程退出前）。
///
/// **背景**：LiteDB 5.0.21 为 **MIT** 许可（nuget.org 明示），非 GPL，
/// 引入不产生任何传染性许可义务。
/// </summary>
public sealed class DatabaseService : IDisposable
{
    /// <summary>
    /// 按连接路径缓存的连接实例。
    /// 用 <see cref="ConcurrentDictionary"/>：连接可能被不同线程首次索取，
    /// 而 LiteDB 的连接对象本身是线程安全的，不必额外加锁。
    /// </summary>
    private readonly ConcurrentDictionary<string, LiteDatabase> _instances = new(StringComparer.Ordinal);

    /// <summary>已释放标志：Dispose 后再索取连接会明确报错，而不是静默重新打开一个。</summary>
    private bool _disposed;

    /// <summary>
    /// 取得指定路径的数据库连接；若尚不存在则创建并缓存。
    /// 文件不存在时 LiteDB 会自行建库。
    /// </summary>
    /// <exception cref="ArgumentException">连接路径为空或全为空白。</exception>
    /// <exception cref="ObjectDisposedException">服务已释放。</exception>
    public LiteDatabase GetConnection(string connectionPath)
    {
        if (string.IsNullOrWhiteSpace(connectionPath))
            throw new ArgumentException("Connection string canot be null or whitespace.", nameof(connectionPath));

        if (_disposed)
            throw new ObjectDisposedException(nameof(DatabaseService));

        return _instances.GetOrAdd(connectionPath, cp => new LiteDatabase(cp));
    }

    /// <summary>当前已缓存的连接数（诊断用：正常情况下应等于"用过的库数量"）。</summary>
    public int ConnectionCount => _instances.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var instance in _instances.Values)
        {
            instance.Dispose();
        }

        _instances.Clear();
        GC.SuppressFinalize(this);
    }
}