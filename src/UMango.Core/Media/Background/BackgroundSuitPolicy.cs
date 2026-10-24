// SPDX-License-Identifier: Apache-2.0
//
// 动态背景的「铺放方式」（PCL 叫「背景 suit 模式」）。
//
// 设计语义借鉴 PCL-CE 的 `Plain Craft Launcher 2/Modules/Base/ModSetup.cs:278`
// 的 `UiBackgroundSuit(int value)`（受限许可目录 → 只借鉴语义，独立重写）。
//
// PCL 原版的取值：
//   0 = 智能、1 = 居中、2 = 适应、3 = 拉伸、4 = 平铺、5 = 左上、6 = 右上、7 = 左下、8 = 右下
//
// 关键语义（PCL :284-291）：**智能 = 条件判定**，不是另一种铺法 ——
//   若「图片宽 < 容器宽的一半」且「图片高 < 容器高的一半」→ 改用**平铺**；
//   否则 → 改用**适应**。
//
// UMango 把这层判定放到 Core（纯函数、可单测），App 层只负责把结论落到
// `ImageBrush` / `MediaElement` 上。

namespace UMango.Core.Media.Background;

/// <summary>背景的铺放方式。</summary>
public enum BackgroundSuitMode
{
    /// <summary>智能：图片小于容器一半则平铺，否则适应。</summary>
    Smart = 0,

    /// <summary>居中：按原始尺寸居中，不缩放。</summary>
    Center = 1,

    /// <summary>适应：等比缩放铺满（可能裁切）。</summary>
    Fit = 2,

    /// <summary>拉伸：非等比拉伸铺满。</summary>
    Stretch = 3,

    /// <summary>平铺：按原始尺寸重复铺。</summary>
    Tile = 4,

    /// <summary>左上角对齐。</summary>
    TopLeft = 5,

    /// <summary>右上角对齐。</summary>
    TopRight = 6,

    /// <summary>左下角对齐。</summary>
    BottomLeft = 7,

    /// <summary>右下角对齐。</summary>
    BottomRight = 8,
}

/// <summary>一次铺放判定的输入。</summary>
/// <param name="ImageWidth">图片原始宽（像素）。</param>
/// <param name="ImageHeight">图片原始高（像素）。</param>
/// <param name="ContainerWidth">容器宽（像素）。</param>
/// <param name="ContainerHeight">容器高（像素）。</param>
public readonly record struct BackgroundSuitInput(
    double ImageWidth,
    double ImageHeight,
    double ContainerWidth,
    double ContainerHeight);

/// <summary>
/// 背景铺放方式的判定：**纯函数**。
/// </summary>
public static class BackgroundSuitPolicy
{
    /// <summary>
    /// 判定实际使用的铺放方式。
    /// </summary>
    /// <param name="mode">用户选的模式。</param>
    /// <param name="input">图片与容器的尺寸。</param>
    /// <returns>实际使用的铺放方式（<see cref="BackgroundSuitMode.Smart"/> 会被解析成具体模式）。</returns>
    public static BackgroundSuitMode Resolve(BackgroundSuitMode mode, BackgroundSuitInput input)
    {
        if (mode != BackgroundSuitMode.Smart) return mode;

        // PCL 原版：图片宽高**都**小于容器的一半 → 平铺；否则 → 适应。
        var halfWidth = input.ContainerWidth / 2d;
        var halfHeight = input.ContainerHeight / 2d;

        var smallerThanHalf = input.ImageWidth < halfWidth && input.ImageHeight < halfHeight;

        return smallerThanHalf ? BackgroundSuitMode.Tile : BackgroundSuitMode.Fit;
    }

    /// <summary>
    /// 该模式是否要按**原始尺寸**渲染（不缩放）。
    ///
    /// 对应 PCL：`Stretch.None` 的那几种（居中 / 平铺 / 四角）都要按原尺寸；
    /// 「适应」「拉伸」则要铺满容器。
    /// </summary>
    public static bool UsesOriginalSize(BackgroundSuitMode mode) => mode switch
    {
        BackgroundSuitMode.Center => true,
        BackgroundSuitMode.Tile => true,
        BackgroundSuitMode.TopLeft => true,
        BackgroundSuitMode.TopRight => true,
        BackgroundSuitMode.BottomLeft => true,
        BackgroundSuitMode.BottomRight => true,
        _ => false,
    };

    /// <summary>
    /// 该模式是否要**铺满容器**（拉伸到容器尺寸）。
    /// </summary>
    public static bool FillsContainer(BackgroundSuitMode mode) => mode switch
    {
        BackgroundSuitMode.Fit => true,
        BackgroundSuitMode.Stretch => true,
        BackgroundSuitMode.Tile => true,
        _ => false,
    };

    /// <summary>
    /// 该模式是否是**角落对齐**（需要指定水平/垂直对齐方式）。
    /// </summary>
    public static bool IsCornerAligned(BackgroundSuitMode mode) => mode switch
    {
        BackgroundSuitMode.TopLeft or BackgroundSuitMode.TopRight
            or BackgroundSuitMode.BottomLeft or BackgroundSuitMode.BottomRight => true,
        _ => false,
    };
}
