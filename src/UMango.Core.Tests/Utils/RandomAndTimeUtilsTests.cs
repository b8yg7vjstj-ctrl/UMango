// SPDX-License-Identifier: Apache-2.0
//
// RandomUtils / TimeUtils 的单元测试（PCL.Core Apache-2.0 逐字采用）。

using System.Globalization;
using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests.Utils;

[Collection("静态状态")]
public class RandomUtilsTests
{
    [Fact]
    public void PickRandom返回集合中的元素()
    {
        var set = new HashSet<int> { 1, 2, 3 };
        Assert.Contains(RandomUtils.PickRandom(set), set);
    }

    [Fact]
    public void PickRandom空集合抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RandomUtils.PickRandom(Array.Empty<int>()));
    }

    [Fact]
    public void PickRandom空集合抛ArgumentException而不是ArgumentNullException()
    {
        // 空集合与 null 集合是两种不同错误，不能混为一谈。
        Assert.Throws<ArgumentException>(() => RandomUtils.PickRandom(new List<int>()));
    }

    [Fact]
    public void PickRandom空引用抛ArgumentNullException()
    {
        // 原版这里会抛 NullReferenceException（不是参数校验异常），
        // 与文档「collection 为 null 时抛 ArgumentNullException」不符 —— 已修。
        Assert.Throws<ArgumentNullException>(() => RandomUtils.PickRandom<int>(null!));
    }

    [Fact]
    public void NextInt范围是闭区间()
    {
        for (var i = 0; i < 200; i++)
        {
            var v = RandomUtils.NextInt(3, 5);
            Assert.InRange(v, 3, 5);
        }
    }

    [Fact]
    public void NextInt可以返回两端()
    {
        var seenMin = false;
        var seenMax = false;
        for (var i = 0; i < 500 && !(seenMin && seenMax); i++)
        {
            var v = RandomUtils.NextInt(0, 1);
            if (v == 0) seenMin = true;
            if (v == 1) seenMax = true;
        }

        Assert.True(seenMin);
        Assert.True(seenMax);
    }

    [Fact]
    public void NextInt下限大于上限抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RandomUtils.NextInt(5, 1));
    }

    [Fact]
    public void Shuffle不改变原列表()
    {
        var list = new List<int> { 1, 2, 3, 4, 5 };
        var shuffled = RandomUtils.Shuffle(list);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, list);
        Assert.Equal(5, shuffled.Count);
        Assert.Equal(list.OrderBy(x => x), shuffled.OrderBy(x => x));
    }

    [Fact]
    public void Shuffle确实打乱了顺序()
    {
        // 单元素必然不变，用 50 个元素测：500 次洗牌后至少有一次顺序与原序列不同。
        var list = Enumerable.Range(0, 50).ToList();
        var changed = false;
        for (var i = 0; i < 500 && !changed; i++)
            changed = !RandomUtils.Shuffle(list).SequenceEqual(list);
        Assert.True(changed);
    }

    [Fact]
    public void ShuffleInPlace原地打乱且元素集合不变()
    {
        var list = Enumerable.Range(0, 20).ToList();
        var before = list.OrderBy(x => x).ToList();
        RandomUtils.ShuffleInPlace(list);
        Assert.Equal(before, list.OrderBy(x => x));
    }

    [Fact]
    public void ShuffleInPlace空引用抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RandomUtils.ShuffleInPlace<int>(null!));
    }
}

[Collection("静态状态")]
public class TimeUtilsTests
{
    [Fact]
    public void GetTimeNow格式为时分秒毫秒()
    {
        var s = TimeUtils.GetTimeNow();
        Assert.Equal(12, s.Length);
        Assert.Equal(':', s[2]);
        Assert.Equal(':', s[5]);
        Assert.Equal('.', s[8]);
    }

    [Fact]
    public void GetTimeTick是正数()
    {
        Assert.True(TimeUtils.GetTimeTick() > 0);
    }

    [Fact]
    public void GetUnixTimestamp接近当前时间()
    {
        var now = TimeUtils.GetUnixTimestamp();
        var diff = Math.Abs(now - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True(diff <= 2, $"时间戳偏差过大：{diff}");
    }

    [Fact]
    public void FromUnixTimestamp往返一致()
    {
        var ts = 1_700_000_000L;
        var dto = TimeUtils.FromUnixTimestamp(ts);
        Assert.Equal(ts, dto.ToUnixTimeSeconds());
    }

    [Fact]
    public void FromUnixTimestamp负数抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUtils.FromUnixTimestamp(-1));
    }

    [Fact]
    public void FromUnixTimestamp过大抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeUtils.FromUnixTimestamp(long.MaxValue));
    }

    [Fact]
    public void ToLocalTime保留时刻()
    {
        var utc = DateTimeOffset.UtcNow;
        var local = TimeUtils.ToLocalTime(utc);
        Assert.Equal(utc.ToUnixTimeSeconds(), local.ToUnixTimeSeconds());
    }

    [Fact]
    public void FormatUnixTimestamp使用当前区域格式()
    {
        var ts = 1_700_000_000L;
        var expected = TimeUtils.FromUnixTimestamp(ts).ToString("g", CultureInfo.CurrentCulture);
        Assert.Equal(expected, TimeUtils.FormatUnixTimestamp(ts));
    }
}
