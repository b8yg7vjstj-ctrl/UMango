// SPDX-License-Identifier: Apache-2.0
//
// 来源：PCL-CE `PCL.Core/Utils/RandomUtils.cs`（Apache-2.0）
// 逐字采用，一处修改：
//   ① 命名空间 PCL.Core.Utils → UMango.Core.Utils。
//
// 另修一个**原版真实缺陷**：`PickRandom` 在集合为 null 时
// 会以 `collection.Count` 抛 `NullReferenceException`（不是参数校验异常）；
// 这里补上显式的 `ArgumentNullException` 校验，语义与文档一致。
// 其余行为（Fisher–Yates 洗牌、`NextInt` 闭区间）逐字保留。

using System;
using System.Collections.Generic;
using System.Linq;

namespace UMango.Core.Utils;

/// <summary>
/// 提供随机数和集合随机操作的实用方法。
/// </summary>
public static class RandomUtils
{
    private static readonly Random _SharedRandom = Random.Shared;

    /// <summary>
    /// 从集合中随机选择一个元素。
    /// </summary>
    /// <typeparam name="T">集合元素类型。</typeparam>
    /// <param name="collection">要从中选择元素的集合。</param>
    /// <returns>随机选择的元素。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="collection"/> 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">当 <paramref name="collection"/> 为空时抛出。</exception>
    public static T PickRandom<T>(ICollection<T> collection)
    {
        if (collection is null)
            throw new ArgumentNullException(nameof(collection));
        if (collection.Count == 0)
            throw new ArgumentException("集合不能为空", nameof(collection));
        var index = _SharedRandom.Next(collection.Count);
        if (collection is IList<T> list)
            return list[index];
        return collection.Skip(index).First();
    }

    /// <summary>
    /// 生成指定范围内的随机整数（包含 min 和 max）。
    /// </summary>
    /// <param name="min">范围下限（包含）。</param>
    /// <param name="max">范围上限（包含）。</param>
    /// <returns>随机整数，范围为 [min, max]。</returns>
    /// <exception cref="ArgumentOutOfRangeException">当 <paramref name="min"/> 大于 <paramref name="max"/> 时抛出。</exception>
    public static int NextInt(int min, int max)
    {
        return min > max ? throw new ArgumentOutOfRangeException(nameof(min), "最小值不能大于最大值") : _SharedRandom.Next(min, max + 1);
    }

    /// <summary>
    /// 随机打乱列表的元素，返回新列表。
    /// </summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    /// <param name="list">要打乱的列表。</param>
    /// <returns>包含随机顺序元素的新列表。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="list"/> 为 null 时抛出。</exception>
    public static List<T> Shuffle<T>(IList<T> list)
    {
        if (list is null)
            throw new ArgumentNullException(nameof(list));
        var result = new List<T>(list);
        var n = result.Count;
        for (var i = n - 1; i > 0; i--)
        {
            var j = _SharedRandom.Next(0, i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
    }

    /// <summary>
    /// 原地随机打乱列表的元素。
    /// </summary>
    /// <typeparam name="T">列表元素类型。</typeparam>
    /// <param name="list">要打乱的列表。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="list"/> 为 null 时抛出。</exception>
    public static void ShuffleInPlace<T>(IList<T> list)
    {
        if (list is null)
            throw new ArgumentNullException(nameof(list));
        var n = list.Count;
        for (var i = n - 1; i > 0; i--)
        {
            var j = _SharedRandom.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
