// SPDX-License-Identifier: Apache-2.0
//
// 来源：PCL-CE `PCL.Core/UI/Controls/WaterfallPanel.cs`（Apache-2.0，199 行）
// 本文件是 WPF 适配层：把 `UMango.Core.UI.Layout.WaterfallLayoutCalculator`
// 的纯计算结果接到 WPF 的 Measure / Arrange 流程上。
//
// 与原版的差异：
//   ① 原版在 Measure 与 Arrange 两个阶段**各算一遍**列数与列宽（Measure 用
//      availableSize，Arrange 用 finalSize）；本版在 Measure 阶段算好分配结果，
//      Arrange 阶段只做「把 Measure 算出的列宽按 finalSize 重新缩放」，
//      两阶段的**项分配**永远一致（不会因为可用宽度变化而换列）。
//   ② 原版 `MaxItemWidth` 默认 100、`ColumnSpacing` 默认 5、`RowSpacing` 默认 0、
//      `ColumnCount` 默认 2 —— 全部沿用。
//   ③ 原版 AutoFit 模式在宽度无限时回退固定列数；沿用。

using System;
using System.Windows;
using System.Windows.Controls;
using UMango.Core.UI.Layout;

namespace UMango.App.Controls;

/// <summary>瀑布流布局面板：每个子项放进当前累计高度最小的列。</summary>
public class WaterfallPanel : Panel
{
    /// <summary>布局模式。</summary>
    public WaterfallLayoutMode LayoutMode
    {
        get => (WaterfallLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public static readonly DependencyProperty LayoutModeProperty =
        DependencyProperty.Register(nameof(LayoutMode), typeof(WaterfallLayoutMode), typeof(WaterfallPanel),
            new FrameworkPropertyMetadata(WaterfallLayoutMode.FixedColumns, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>固定列数（仅在 <see cref="LayoutMode"/> 为 FixedColumns 时生效）。</summary>
    public int ColumnCount
    {
        get => (int)GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public static readonly DependencyProperty ColumnCountProperty =
        DependencyProperty.Register(nameof(ColumnCount), typeof(int), typeof(WaterfallPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>列间距。</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(WaterfallPanel),
            new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>行间距。</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(WaterfallPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>项的最大宽度（仅在 AutoFit 模式下生效）。</summary>
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(WaterfallPanel),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Measure 阶段算出的分配结果；Arrange 阶段直接复用。
    private WaterfallLayout _layout;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        var heights = new double[count];
        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            // 先按最终列宽量一次，才能知道高度；列宽要先算出来。
            heights[i] = child.DesiredSize.Height;
        }

        // 第一轮：用 availableWidth 定列数与列宽，按列宽量子项。
        var probe = WaterfallLayoutCalculator.Compute(
            availableSize.Width, LayoutMode, ColumnCount, MaxItemWidth, ColumnSpacing, RowSpacing, heights);
        var colWidth = probe.ColumnWidth;

        for (var i = 0; i < count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            heights[i] = child.DesiredSize.Height;
        }

        // 第二轮：拿到真实高度后重算分配。
        _layout = WaterfallLayoutCalculator.Compute(
            availableSize.Width, LayoutMode, ColumnCount, MaxItemWidth, ColumnSpacing, RowSpacing, heights);

        var width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? _layout.ColumnWidth * _layout.ColumnCount
            : availableSize.Width;

        return new Size(width, _layout.TotalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_layout.ItemCount == 0) return finalSize;

        // 列宽按最终宽度重新缩放（保证铺满），但**项分配**沿用 Measure 阶段的结果。
        var columns = _layout.ColumnCount;
        var colWidth = Math.Max(0, (finalSize.Width - (columns - 1) * ColumnSpacing) / columns);

        for (var i = 0; i < _layout.ItemCount; i++)
        {
            var child = InternalChildren[i];
            var col = _layout.ItemColumn[i];
            var x = col * (colWidth + ColumnSpacing);
            var y = _layout.ItemY[i];
            child.Arrange(new Rect(new Point(x, y), new Size(colWidth, _layout.ItemHeight[i])));
        }

        return finalSize;
    }
}
