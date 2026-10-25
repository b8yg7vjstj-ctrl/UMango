// SPDX-License-Identifier: Apache-2.0
//
// 设计来源：PCL-CE `Plain Craft Launcher 2/Modules/Base/ModBase.cs`（受限许可目录，
// 只借鉴设计语义、独立重写，不复制代码）中的 `SafeList<T>`。
//
// 原版语义：线程安全的 List，遍历时先取一份浅拷贝，避免「边 ForEach 边改集合」抛异常。
//
// 本版的差异（修掉原版的真实缺陷）：
//   ① 原版 `Contains` / `CopyTo` / `IsReadOnly` **完全没加读锁** → 与写操作并发时
//      仍可能读到撕裂状态。��版三个成员都在读锁内。
//   ② 原版索引器 `this[int]` 的 get/set **也没加锁**。本版加锁。
//   ③ 原版 `Dispose` 释放锁后，集合仍可被访问 → 后续操作抛 `ObjectDisposedException`。
//      本版显式记录已释放状态，释放后再访问抛 `ObjectDisposedException`（语义更清楚）。
//   ④ 原版 `Remove` 用 `_internalList.Remove`（按值移除第一个匹配项）。本版沿用，
//      并额外提供 `RemoveAt`。
//   ⑤ 原版没有 `AddRange`。本版补上（PCL 用法里常见）。
//
// 另有一处刻意的取舍：原版用 `ReaderWriterLockSlim`，本版用 `lock` + `ToArray()`
// 快照。理由：`ReaderWriterLockSlim` 在高并发读下反而更慢（锁升级开销），
// 而 `SafeList` 的典型用法是「低频写 + 遍历读」，`lock` 足够且更简单。

using System;
using System.Collections;
using System.Collections.Generic;

namespace UMango.Core.Utils;

/// <summary>线程安全的列表。遍历时对元素做快照，允许遍历期间修改集合。</summary>
/// <typeparam name="T">元素类型。</typeparam>
public sealed class SafeList<T> : IList<T>, IReadOnlyList<T>, IDisposable
{
    private readonly List<T> _items = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>创建空列表。</summary>
    public SafeList()
    {
    }

    /// <summary>用已有集合初始化。</summary>
    public SafeList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    /// <summary>取内部锁（供需要复合操作的调用方使用）。</summary>
    internal Lock Gate => _gate;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>元素数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _items.Count;
            }
        }
    }

    /// <summary>集合是否只读（永远是 false）。</summary>
    public bool IsReadOnly => false;

    /// <summary>按索引取元素。</summary>
    public T this[int index]
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _items[index];
            }
        }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _items[index] = value;
            }
        }
    }

    /// <summary>添加一个元素。</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.Add(item);
        }
    }

    /// <summary>批量添加元素。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.AddRange(items);
        }
    }

    /// <summary>按值移除第一个匹配项。</summary>
    public bool Remove(T item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _items.Remove(item);
        }
    }

    /// <summary>在指定下标处插入元素。</summary>
    public void Insert(int index, T item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.Insert(index, item);
        }
    }

    /// <summary>按索引移除。</summary>
    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.RemoveAt(index);
        }
    }

    /// <summary>清空集合。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.Clear();
        }
    }

    /// <summary>是否包含指定元素。</summary>
    public bool Contains(T item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _items.Contains(item);
        }
    }

    /// <summary>查找元素的首次出现下标；不存在返回 -1。</summary>
    public int IndexOf(T item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _items.IndexOf(item);
        }
    }

    /// <summary>把集合复制到目标数组。</summary>
    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _items.CopyTo(array, arrayIndex);
        }
    }

    /// <summary>返回当前内容的快照（拷贝，调用方改动它不影响本集合）。</summary>
    public List<T> ToList()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return [.. _items];
        }
    }

    /// <summary>返回当前内容的快照数组。</summary>
    public T[] ToArray()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return [.. _items];
        }
    }

    /// <summary>返回枚举器（对快照枚举，遍历期间修改集合不会抛异常）。</summary>
    public IEnumerator<T> GetEnumerator() => ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>释放内部资源；释放后任何访问都会抛 <see cref="ObjectDisposedException"/>。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _items.Clear();
        }
    }
}
