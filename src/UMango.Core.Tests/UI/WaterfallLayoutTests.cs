// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using UMango.Core.UI.Layout;
using Xunit;

namespace UMango.Core.Tests.UI;

public class WaterfallLayoutTests
{
    private static double[] Heights(params double[] h) => h;

    [Fact]
    public void 固定列数模式_列数至少为一()
    {
        Assert.Equal(1, WaterfallLayoutCalculator.ResolveColumnCount(
            500, WaterfallLayoutMode.FixedColumns, 0, 100, 5));
        Assert.Equal(1, WaterfallLayoutCalculator.ResolveColumnCount(
            500, WaterfallLayoutMode.FixedColumns, -3, 100, 5));
    }

    [Fact]
    public void 自适应模式_按可用宽度算列数()
    {
        // 槽宽 = 100 + 5 = 105；(300 + 5) / 105 = 2.90 → 2 列
        Assert.Equal(2, WaterfallLayoutCalculator.ResolveColumnCount(
            300, WaterfallLayoutMode.AutoFit, 2, 100, 5));
        // 刚好容纳 3 列：(310 + 5) / 105 = 3.0 → 3 列
        Assert.Equal(3, WaterfallLayoutCalculator.ResolveColumnCount(
            310, WaterfallLayoutMode.AutoFit, 2, 100, 5));
    }

    [Fact]
    public void 自适应模式_宽度无限时回退固定列数()
    {
        Assert.Equal(4, WaterfallLayoutCalculator.ResolveColumnCount(
            double.PositiveInfinity, WaterfallLayoutMode.AutoFit, 4, 100, 5));
    }

    [Fact]
    public void 自适应模式_最大项宽非法时用兜底值()
    {
        // maxItemWidth = 0 → 兜底 100，槽宽 105，(310+5)/105 = 3
        Assert.Equal(3, WaterfallLayoutCalculator.ResolveColumnCount(
            310, WaterfallLayoutMode.AutoFit, 1, 0, 5));
    }

    [Fact]
    public void 布局_把项放进当前最矮的列()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 2, 100, 0, 0, Heights(100, 20, 20));

        // 第 1 项 -> 列 0（高 100）；第 2 项 -> 列 1（高 20，更矮）；第 3 项 -> 列 1（高 20+20=40 < 100）
        Assert.Equal(new[] { 0, 1, 1 }, layout.ItemColumn);
        Assert.Equal(new[] { 0d, 0d, 20d }, layout.ItemY);
        Assert.Equal(100, layout.TotalHeight);
    }

    [Fact]
    public void 布局_同高时优先更靠左的列()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 3, 100, 0, 0, Heights(50, 50, 50, 50, 50, 50));

        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2 }, layout.ItemColumn);
    }

    [Fact]
    public void 布局_列宽按可用宽度均分并扣除列间距()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            210, WaterfallLayoutMode.FixedColumns, 2, 100, 10, 0, Heights(10));

        Assert.Equal(100, layout.ColumnWidth);
        Assert.Equal(new[] { 0d, 110d }, layout.ColumnX);
    }

    [Fact]
    public void 布局_行间距只算一次()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 1, 100, 0, 10, Heights(50, 50));

        // 第 2 项 y = 50 + 10 = 60；总高度 = 110（不是 120）
        Assert.Equal(60, layout.ItemY[1]);
        Assert.Equal(110, layout.TotalHeight);
    }

    [Fact]
    public void 布局_空列表总高度为零()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 2, 100, 5, 5, Array.Empty<double>());

        Assert.Equal(0, layout.ItemCount);
        Assert.Equal(0, layout.TotalHeight);
    }

    [Fact]
    public void 布局_总高度取最高列()
    {
        var layout = WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 2, 100, 0, 0, Heights(100, 10, 10, 10));

        // 列0: 100；列1: 10+10+10 = 30
        Assert.Equal(100, layout.TotalHeight);
    }

    [Fact]
    public void 布局_高度列表为空引用则抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => WaterfallLayoutCalculator.Compute(
            200, WaterfallLayoutMode.FixedColumns, 2, 100, 5, 5, null!));
    }
}
