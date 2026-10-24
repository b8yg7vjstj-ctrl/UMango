// SPDX-License-Identifier: Apache-2.0
//
// 本文件逐字采用 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Theme/ToneProfile.cs 与 ToneProfileConfig.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. 命名空间 PCL.Core.UI.Theme → UMango.Core.UI.Theme；
//   2. 补了 XML 文档注释（原版只有 record 签名，没有说明各参数的含义）；
//   3. DefaultDark 的显式参数改用命名参数并补齐说明（原版依赖位置参数，
//      一旦上游调���顺序就会静默错位 —— 命名参数能挡住这种改动）。

namespace UMango.Core.UI.Theme;

/// <summary>
/// 色调档：定义一整套「感知均匀」的色阶参数。
///
/// 亮部 L1..L8 是**感知等距**的一组明度值；
/// 彩度 C1..C8 是与之对应的彩度；
/// 前景/背景/白色等语义色与若干半透明遮罩的透明度也在此定义。
/// 生成整套调色板时，这些参数被送进 OKLCH 生成对应颜色。
/// </summary>
public record ToneProfile(
    /// <summary>最深的一档明度（0~1）。</summary>
    double L1 = 0.35,
    /// <summary>第二档明度。</summary>
    double L2 = 0.5,
    /// <summary>第三档明度。</summary>
    double L3 = 0.575,
    /// <summary>第四档明度。</summary>
    double L4 = 0.65,
    /// <summary>第五档明度。</summary>
    double L5 = 0.8,
    /// <summary>第六档明度。</summary>
    double L6 = 0.92,
    /// <summary>第七档明度。</summary>
    double L7 = 0.94,
    /// <summary>第八档明度（最浅）。</summary>
    double L8 = 0.96,
    /// <summary>纯白档的明度。</summary>
    double LWhite = 1,
    /// <summary>前景色的明度。</summary>
    double LForeground = 0,
    /// <summary>背景色的明度。</summary>
    double LBackground = 0.995,
    /// <summary>第一档彩度。</summary>
    double C1 = 0.025,
    /// <summary>第二档彩度（主色）。</summary>
    double C2 = 0.188,
    /// <summary>第三档彩度。</summary>
    double C3 = 0.213,
    /// <summary>第四档彩度。</summary>
    double C4 = 0.168,
    /// <summary>第五档彩度。</summary>
    double C5 = 0.093,
    /// <summary>第六档彩度。</summary>
    double C6 = 0.036,
    /// <summary>第七档彩度。</summary>
    double C7 = 0.028,
    /// <summary>第八档彩度。</summary>
    double C8 = 0.018,
    /// <summary>半透明白的透明度。</summary>
    double ASemiWhite = 0.733,
    /// <summary>四分之一透明白的透明度。</summary>
    double AHalfWhite = 0.333,
    /// <summary>半透明色（几乎不可见）的透明度。</summary>
    double ASemiTransparent = 0.004,
    /// <summary>半透明色的透明度。</summary>
    double AHalfTransparent = 0.5,
    /// <summary>全透明的透明度。</summary>
    double ATransparent = 0,
    /// <summary>背景遮罩的透明度。</summary>
    double ABackground = 0.824,
    /// <summary>提示框背景的透明度。</summary>
    double AToolTip = 0.9
);

/// <summary>亮色与暗色两套色调档。</summary>
public record ToneProfileConfig
{
    /// <summary>亮色主题的默认色调档。</summary>
    public static readonly ToneProfile DefaultLight = new();

    /// <summary>暗色主题的默认色调档。</summary>
    public static readonly ToneProfile DefaultDark = new(
        L1: 0.96, L2: 0.75, L3: 0.6, L4: 0.65,
        L5: 0.45, L6: 0.25, L7: 0.225, L8: 0.2,
        LBackground: 0.3, LForeground: 1, LWhite: 0.275
    );

    /// <summary>亮色主题的色调档（可覆盖）。</summary>
    public ToneProfile Light { get => field ?? DefaultLight; init; } = null!;

    /// <summary>暗色主题的色调档（可覆盖）。</summary>
    public ToneProfile Dark { get => field ?? DefaultDark; init; } = null!;
}
