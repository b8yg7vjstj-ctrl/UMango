using System;
using LiteDB;

namespace UMango.Core.Database;

/// <summary>
/// 数据库实体基类 —— 采用自 PCL-CE <c>PCL.Core/App/Database/DatabaseEntry.cs</c>。
///
/// **来源与许可**：原文件属于 PCL-CE 的 <c>PCL.Core/</c>（Apache-2.0）。
/// 原类 47 行，逻辑全是样板（空值校验 + 幂等 Dispose），不表达任何业务语义，
/// 故按「Apache-2.0 可复用」采用，**结构与命名逐字保留**
/// （<c>Db</c> / <c>connPath</c> / <c>Dispose(bool)</c> / <c>GC.SuppressFinalize</c>）。
///
/// **与 PCL 原版的唯一实质差异**：幂等闸门的位置。
/// 原版把 <c>if (_disposed) return;</c> 写在 <c>Dispose(bool)</c> 方法体里 ——
/// 而该方法是 <c>virtual</c> 的，子类 override 后，
/// 基类这道闸门保护不了子类写在 <c>base.Dispose(disposing)</c> **之后**的代码。
/// 本仓库实测到此问题：子类连调三次 <c>Dispose()</c>，清理逻辑会跑三次
/// （<c>DatabaseServiceTests.派生释放逻辑只执行一次</c> 守着这条）。
/// 故 UMango 把闸门提到**非虚**的 <see cref="Dispose"/> 入口 ——
/// 子类无论怎么实现 <c>Dispose(bool)</c> 都绕不过它。
/// </summary>
public abstract class DatabaseEntry : IDisposable
{
    /// <summary>底层连接。**不要 Dispose 它** —— 由 <see cref="DatabaseService"/> 按路径统一托管。</summary>
    protected readonly LiteDatabase Db;

    private bool _disposed;

    /// <exception cref="ArgumentNullException">服务为 null。</exception>
    /// <exception cref="ArgumentException">连接路径无效（为空或全空白）。</exception>
    protected DatabaseEntry(DatabaseService service, string connPath)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        Db = service.GetConnection(connPath);
    }

    /// <exception cref="ArgumentNullException">传入的连接为 null。</exception>
    protected DatabaseEntry(LiteDatabase database)
    {
        Db = database ?? throw new ArgumentNullException(nameof(database), "Database variable can not be null.");
    }

    /// <summary>是否已释放。子类释放派生资源时可用它避免重复清理。</summary>
    protected bool IsDisposed => _disposed;

    public void Dispose()
    {
        // 幂等闸门只在非虚入口 —— 子类绕不过它
        if (_disposed) return;
        _disposed = true;

        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 子类在此释放派生资源（流、游标等）。
    /// 务必不要释放 <see cref="Db"/>：它由 <see cref="DatabaseService"/> 按连接路径共享管理。
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}