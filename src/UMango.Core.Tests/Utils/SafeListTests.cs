// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests.Utils;

public class SafeListTests
{
    [Fact]
    public void 添加与读取()
    {
        using var list = new SafeList<int> { 1, 2, 3 };
        Assert.Equal(3, list.Count);
        Assert.Equal(2, list[1]);
    }

    [Fact]
    public void 构造器接受已有集合()
    {
        using var list = new SafeList<string>(["a", "b"]);
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0]);
    }

    [Fact]
    public void 构造器拒绝null()
    {
        Assert.Throws<ArgumentNullException>(() => new SafeList<int>(null!));
    }

    [Fact]
    public void 移除与清空()
    {
        using var list = new SafeList<int> { 1, 2, 3 };
        Assert.True(list.Remove(2));
        Assert.False(list.Remove(99));
        Assert.Equal(2, list.Count);
        list.Clear();
        Assert.Empty(list);
    }

    [Fact]
    public void 按索引移除与插入()
    {
        using var list = new SafeList<int> { 1, 3 };
        list.Insert(1, 2);
        Assert.Equal(new[] { 1, 2, 3 }, list.ToList());
        list.RemoveAt(0);
        Assert.Equal(new[] { 2, 3 }, list.ToList());
    }

    [Fact]
    public void 包含与索引查找()
    {
        using var list = new SafeList<string> { "x", "y" };
        Assert.Contains("y", list);
        Assert.DoesNotContain("z", list);
        Assert.Equal(1, list.IndexOf("y"));
        Assert.Equal(-1, list.IndexOf("z"));
    }

    [Fact]
    public void 复制到数组()
    {
        using var list = new SafeList<int> { 7, 8 };
        var array = new int[3];
        list.CopyTo(array, 1);
        Assert.Equal(new[] { 0, 7, 8 }, array);
    }

    [Fact]
    public void 遍历期间修改集合不抛异常()
    {
        using var list = new SafeList<int> { 1, 2, 3, 4 };
        var seen = new List<int>();
        foreach (var x in list)
        {
            seen.Add(x);
            list.Remove(x);
        }
        Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
    }

    [Fact]
    public void 快照与内部状态隔离()
    {
        using var list = new SafeList<int> { 1, 2 };
        var snapshot = list.ToList();
        snapshot.Add(3);
        Assert.Equal(2, list.Count);
        Assert.Equal(2, list.ToArray().Length);
    }

    [Fact]
    public void 释放后访问抛异常()
    {
        var list = new SafeList<int> { 1 };
        list.Dispose();
        Assert.Throws<ObjectDisposedException>(() => list.Add(2));
        Assert.Throws<ObjectDisposedException>(() => list.Count);
        Assert.Throws<ObjectDisposedException>(() => list[0]);
        Assert.Throws<ObjectDisposedException>(() => list.ToList());
        Assert.Throws<ObjectDisposedException>(() => list.ToArray());
        Assert.Throws<ObjectDisposedException>(() => list.Contains(1));
        Assert.Throws<ObjectDisposedException>(() => list.Clear());
        Assert.Throws<ObjectDisposedException>(() => list.Insert(0, 5));
        Assert.Throws<ObjectDisposedException>(() => list.RemoveAt(0));
        Assert.Throws<ObjectDisposedException>(() => list.Remove(1));
        Assert.Throws<ObjectDisposedException>(() => list.IndexOf(1));
        Assert.Throws<ObjectDisposedException>(() => list.AddRange([1]));
    }

    [Fact]
    public void 并发写入不丢数据()
    {
        using var list = new SafeList<int>();
        const int workers = 8, perWorker = 500;
        Parallel.For(0, workers, w =>
        {
            for (var i = 0; i < perWorker; i++) list.Add(w * perWorker + i);
        });
        Assert.Equal(workers * perWorker, list.Count);
    }

    [Fact]
    public async Task 并发读写不抛异常()
    {
        using var list = new SafeList<int>();
        using var cts = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000 && !cts.IsCancellationRequested; i++) list.Add(i);
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000 && !cts.IsCancellationRequested; i++)
            {
                foreach (var x in list) { _ = x; }
            }
        });
        await Task.WhenAll(writer, reader);
        Assert.True(list.Count > 0);
    }

    [Fact]
    public void 索引器赋值生效()
    {
        using var list = new SafeList<int> { 1, 2 };
        list[0] = 42;
        Assert.Equal(42, list[0]);
    }

    [Fact]
    public void 批量添加()
    {
        using var list = new SafeList<int>();
        list.AddRange([1, 2, 3]);
        Assert.Equal(3, list.Count);
        Assert.Throws<ArgumentNullException>(() => list.AddRange(null!));
    }
}
