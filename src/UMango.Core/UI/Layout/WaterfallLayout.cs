// SPDX-License-Identifier: Apache-2.0
//
// 来源：PCL-CE `PCL.Core/UI/Controls/WaterfallPanel.cs`（Apache-2.0，199 行）
// 本文件是**纯算法层**的独立重写：把原版 WPF `Panel` 里的
// 「测量 → 决定列数 → 决定每项落在哪一列 → 决定每列高度」逻辑抽成
// 不依赖 WPF 的纯函数，便于单元测试（App 侧的 WPF 控件只做适配）。
//
// 与原版的差异：
//   ① 原版把列高数组藏在 Panel 的私有字段里，Measure 与 Arrange 两阶段
//      各自重算一遍；本文件把「算列数」「算列宽」「分配项」拆成三步，
//      Measure 与 Arrange 共用同一份分配结果（避免两阶段算出不同布局）。
//   ② 原版 AutoFit 模式在宽度为 Infinity 时回退到固定列数；本文件保留该行为。
//   ③ 原版 `MaxItemWidth <= 0` 时用 100 兜底；本文件保留并显式化为常量。

namespace UMango.Core.UI.Layout;

/// <summary>瀑布流布局模式。</summary>
public enum WaterfallLayoutMode
{
    /// <summary>固定列数模式，使用 <see cref="WaterfallLayout.ColumnCount"/> 指定列数。</summary>
    FixedColumns,

    /// <summary>自适应模式，根据可用宽度和最大项宽自动计算列数。</summary>
    AutoFit
}

/// <summary>
/// 瀑布流布局的纯计算结果。
/// </summary>
/// <param name="ColumnCount">实际使用的列数（至少 1）。</param>
/// <param name="ColumnWidth">每列的宽度（像素）。</param>
/// <param name="ColumnX">每列的 X 坐标（像素）。</param>
/// <param name="ItemColumn">每个项落在第几列（索引与输入下标一一对应）。</param>
/// <param name="ItemY">每个项的 Y 坐标（像素）。</param>
/// <param name="ItemHeight">每个项的高度（像素）。</param>
/// <param name="TotalHeight">整个面板需要的高度（像素）。</param>
public readonly record struct WaterfallLayout(
    int ColumnCount,
    double ColumnWidth,
    IReadOnlyList<double> ColumnX,
    IReadOnlyList<int> ItemColumn,
    IReadOnlyList<double> ItemY,
    IReadOnlyList<double> ItemHeight,
    double TotalHeight)
{
    /// <summary>项数量。</summary>
    public int ItemCount => ItemColumn.Count;
}

/// <summary>
/// 瀑布流布局计算器。
///
/// 语义与 PCL-CE 的 <c>WaterfallPanel</c> 一致：每个子项放进**当前累计高度最小**的列，
/// 因此各列高度大致均衡（而不是严格按索引轮流分列）。
/// </summary>
public static class WaterfallLayoutCalculator
{
    /// <summary>最大项宽非法时使用的兜底值。</summary>
    public const double FallbackMaxItemWidth = 100.0;

    /// <summary>决定实际列数。</summary>
    /// <param name="availableWidth">可用宽度；<see cref="double.PositiveInfinity"/> 表示宽度不限。</param>
    /// <param name="mode">布局模式。</param>
    /// <param name="columnCount">固定列数模式下的列数。</param>
    /// <param name="maxItemWidth">自适应模式下的最大项宽。</param>
    /// <param name="columnSpacing">列间距。</param>
    /// <returns>列数（至少 1）。</returns>
    public static int ResolveColumnCount(
        double availableWidth,
        WaterfallLayoutMode mode,
        int columnCount,
        double maxItemWidth,
        double columnSpacing)
    {
        if (mode == WaterfallLayoutMode.FixedColumns)
            return Math.Max(1, columnCount);

        // 宽度无限时无法自适应，回退到固定列数。
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return Math.Max(1, columnCount);

        var effectiveMaxWidth = maxItemWidth > 0 ? maxItemWidth : FallbackMaxItemWidth;
        var slotWidth = effectiveMaxWidth + columnSpacing;
        if (slotWidth <= 0) return Math.Max(1, columnCount);

        return Math.Max(1, (int)Math.Floor((availableWidth + columnSpacing) / slotWidth));
    }

    /// <summary>
    /// 计算布局。
    /// </summary>
    /// <param name="availableWidth">可用宽度。</param>
    /// <param name="mode">布局模式。</param>
    /// <param name="columnCount">固定列数模式下的列数。</param>
    /// <param name="maxItemWidth">自适应模式下的最大项宽。</param>
    /// <param name="columnSpacing">列间距。</param>
    /// <param name="rowSpacing">行间距。</param>
    /// <param name="itemHeights">每个子项的高度（按显示顺序）。</param>
    public static WaterfallLayout Compute(
        double availableWidth,
        WaterfallLayoutMode mode,
        int columnCount,
        double maxItemWidth,
        double columnSpacing,
        double rowSpacing,
        IReadOnlyList<double> itemHeights)
    {
        ArgumentNullException.ThrowIfNull(itemHeights);

        var columns = ResolveColumnCount(availableWidth, mode, columnCount, maxItemWidth, columnSpacing);

        var width = double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)
            ? 0
            : availableWidth;
        var colWidth = Math.Max(0, (width - (columns - 1) * columnSpacing) / columns);

        var colX = new double[columns];
        for (var i = 0; i < columns; i++)
            colX[i] = i * (colWidth + columnSpacing);

        var itemColumn = new int[itemHeights.Count];
        var itemY = new double[itemHeights.Count];
        var itemHeight = new double[itemHeights.Count];
        var colHeights = new double[columns];

        for (var i = 0; i < itemHeights.Count; i++)
        {
            var target = MinHeightColumn(colHeights);
            itemColumn[i] = target;
            itemY[i] = colHeights[target];
            itemHeight[i] = itemHeights[i];
            colHeights[target] += itemHeights[i] + rowSpacing;
        }

        // 总高度取最高列；每列末尾多算了一次行间距，要减掉。
        var totalHeight = 0d;
        for (var i = 0; i < columns; i++)
        {
            var h = colHeights[i];
            if (h > 0) h -= rowSpacing;
            totalHeight = Math.Max(totalHeight, h);
        }

        return new WaterfallLayout(
            columns, colWidth, colX, itemColumn, itemY, itemHeight, totalHeight);
    }

    private static int MinHeightColumn(double[] colHeights)
    {
        var minIndex = 0;
        var minHeight = colHeights[0];
        for (var i = 1; i < colHeights.Length; i++)
        {
            if (colHeights[i] < minHeight)
            {
                minHeight = colHeights[i];
                minIndex = i;
            }
        }

        return minIndex;
    }
}
