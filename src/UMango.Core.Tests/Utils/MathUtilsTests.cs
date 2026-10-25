// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UMango.Core.Utils;
using Xunit;

namespace UMango.Core.Tests.Utils;

public class MathUtilsTests
{
    [Theory]
    [InlineData("FF", 16, 10, "255")]
    [InlineData("1010", 2, 10, "10")]
    [InlineData("10", 10, 2, "1010")]
    [InlineData("0", 10, 16, "0")]
    [InlineData("7FFFFFFFFFFFFFFF", 16, 10, "9223372036854775807")]
    public void 进制转换_常规值(string input, int from, int to, string expected)
        => Assert.Equal(expected, MathUtils.RadixConvert(input, from, to));

    [Fact]
    public void 进制转换_负号保留()
    {
        Assert.Equal("-255", MathUtils.RadixConvert("-FF", 16, 10));
    }

    [Fact]
    public void 进制转换_空输入返回零()
    {
        Assert.Equal("0", MathUtils.RadixConvert("", 10, 16));
        Assert.Equal("0", MathUtils.RadixConvert(null, 10, 16));
    }

    [Fact]
    public void 进制转换_负号后无数字返回零()
    {
        Assert.Equal("0", MathUtils.RadixConvert("-", 10, 16));
    }

    [Fact]
    public void 进制转换_非法字符抛异常而不是返回空串()
    {
        // 原版 PCL 的实现：IndexOf 得到 -1 → realNum 变负 → while 不执行 → 返回 ""
        var ex = Assert.Throws<ArgumentException>(() => MathUtils.RadixConvert("12G4", 16, 10));
        Assert.Contains("G", ex.Message);
    }

    [Fact]
    public void 进制转换_超出源进制的字符抛异常()
    {
        Assert.Throws<ArgumentException>(() => MathUtils.RadixConvert("9", 8, 10));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(65)]
    public void 进制转换_进制越界抛异常(int radix)
    {
        Assert.Throws<ArgumentException>(() => MathUtils.RadixConvert("1", radix, 10));
        Assert.Throws<ArgumentException>(() => MathUtils.RadixConvert("1", 10, radix));
    }

    [Fact]
    public void 进制转换_超大数不溢出()
    {
        // 16 个 'Z'（base64 数字表里 Z = 35）= 44015645841257965329746639075，远超 long.MaxValue
        var result = MathUtils.RadixConvert("ZZZZZZZZZZZZZZZZ", 64, 10);
        Assert.Equal("44015645841257965329746639075", result);
    }

    [Fact]
    public void 贝塞尔_端点被夹取()
    {
        Assert.Equal(0d, MathUtils.MathBezier(-1, 0.3, 0.7, 0.7, 0.3));
        Assert.Equal(1d, MathUtils.MathBezier(2, 0.3, 0.7, 0.7, 0.3));
    }

    [Fact]
    public void 贝塞尔_NaN输入返回零()
    {
        Assert.Equal(0d, MathUtils.MathBezier(double.NaN, 0.3, 0.7, 0.7, 0.3));
    }

    [Fact]
    public void 贝塞尔_控制点与端点重合时是线性()
    {
        // x1=y1=0, x2=y2=1 → y == x（用高精度容差）
        for (var x = 0.05; x < 1d; x += 0.1)
            Assert.Equal(x, MathUtils.MathBezier(x, 0d, 0d, 1d, 1d, 1e-9), 6);
    }

    [Fact]
    public void 贝塞尔_单调曲线结果落在端点之间()
    {
        var y = MathUtils.MathBezier(0.5, 0.25, 0.1, 0.75, 0.9);
        Assert.InRange(y, 0d, 1d);
    }

    [Fact]
    public void 贝塞尔_退化控制点不抛异常()
    {
        // x1 = x2 = 0 → 曲线退化，导数为 0（牛顿法不收敛）
        var y = MathUtils.MathBezier(0.5, 0d, 0.2, 0d, 0.8);
        Assert.InRange(y, 0d, 1d);
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(255d, 255)]
    [InlineData(-10d, 0)]
    [InlineData(300d, 255)]
    [InlineData(127.4, 127)]
    [InlineData(127.5, 128)]
    public void 数学字节_夹取并四舍五入(double value, int expected)
        => Assert.Equal(expected, MathUtils.MathByte(value));

    [Fact]
    public void 数学字节_NaN返回零()
        => Assert.Equal(0, MathUtils.MathByte(double.NaN));

    [Fact]
    public void 数学百分比_两端取值正确()
    {
        Assert.Equal(10d, MathUtils.MathPercent(10d, 20d, 0d));
        Assert.Equal(20d, MathUtils.MathPercent(10d, 20d, 1d));
        Assert.Equal(15d, MathUtils.MathPercent(10d, 20d, 0.5d));
    }

    [Fact]
    public void 数学夹取_双精度与整型()
    {
        Assert.Equal(5d, MathUtils.MathClamp(10d, 0d, 5d));
        Assert.Equal(0d, MathUtils.MathClamp(-1d, 0d, 5d));
        Assert.Equal(5, MathUtils.MathClamp(9, 0, 5));
        Assert.Equal(0, MathUtils.MathClamp(-9, 0, 5));
    }

    [Fact]
    public void 符号函数()
    {
        Assert.Equal(0, MathUtils.MathSgn(0d));
        Assert.Equal(1, MathUtils.MathSgn(3d));
        Assert.Equal(-1, MathUtils.MathSgn(-3d));
        Assert.Equal(0, MathUtils.MathSgn(double.NaN));
    }
}
