// SPDX-License-Identifier: Apache-2.0
//
// 本文件参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Theme/ThemeService.cs 中的 _CalculateGrays / _CalculateColors。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. **产出纯数据，不碰 WPF 资源字典** —— 原版算出颜色后立刻
//      通过 CatColorResource 写进 Application.Current.Resources，
//      UMango 改成返回不可变的调色板，由 App 层决定怎么用；
//   2. 原版依赖 Wacton.Unicolour 做 OKLAB 转换，UMango 换成自实现的
//      OklabColor（见同目录 OklabColor.cs）；
//   3. 原版的 ColorTheme 枚举（SkyBlue / CatBlue / DeathBlue / HmclBlue）
//      是 PCL 的品牌色，UMango 改成通用的「色相 + 亮度/彩度调整」参数，
//      由调用方给定；
//   4. 原版 _AdjustLinear 的「非对称线性插值」原样保留 ——
//      这是它让同一套色阶能适配不同品牌色的关键。

namespace UMango.Core.UI.Theme;

/// <summary>
/// 调色板生成器：按色调档生成一整套感知均匀的颜色。
///
/// 产出两部分：
///   · 灰阶（Gray1..Gray8、White、HalfWhite、SemiWhite、Background、…）
///   · 彩色阶（1..8、Bg0、Bg1、SemiTransparent）
/// </summary>
public static class PaletteGenerator
{
    /// <summary>生成一整套灰阶颜色。</summary>
    /// <param name="tone">色调档。</param>
    /// <returns>名称 → ARGB 的映射。</returns>
    public static IReadOnlyDictionary<string, int> GenerateGrays(ToneProfile tone)
    {
        ArgumentNullException.ThrowIfNull(tone);

        var palette = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Gray1"] = Gray(tone, tone.L1),
            ["Gray2"] = Gray(tone, tone.L2),
            ["Gray3"] = Gray(tone, tone.L3),
            ["Gray4"] = Gray(tone, tone.L4),
            ["Gray5"] = Gray(tone, tone.L5),
            ["Gray6"] = Gray(tone, tone.L6),
            ["Gray7"] = Gray(tone, tone.L7),
            ["Gray8"] = Gray(tone, tone.L8),
            ["HalfWhite"] = Gray(tone, tone.LWhite, tone.AHalfWhite),
            ["SemiWhite"] = Gray(tone, tone.LWhite, tone.ASemiWhite),
            ["White"] = Gray(tone, tone.LWhite),
            ["Transparent"] = Gray(tone, tone.LWhite, tone.ATransparent),
            ["TransparentBackground"] = Gray(tone, tone.LBackground, tone.ABackground),
            ["Background"] = Gray(tone, tone.LBackground),
            ["ToolTip"] = Gray(tone, tone.LBackground, tone.AToolTip),
            ["RedBack"] = OklabColor.FromLch(tone.L7, 0.25, 30, tone.AHalfTransparent).ToArgb(),
            ["Memory"] = Gray(tone, tone.LForeground),
        };

        return palette;
    }

    /// <summary>
    /// 生成一整套彩色阶。
    /// </summary>
    /// <param name="tone">色调档。</param>
    /// <param name="hue">主色相（度）。</param>
    /// <param name="lightAdjust">明度调整量，-1~1。</param>
    /// <param name="chromaAdjust">彩度调整量，-1~1。</param>
    public static IReadOnlyDictionary<string, int> GenerateColors(
        ToneProfile tone,
        double hue,
        double lightAdjust,
        double chromaAdjust)
    {
        ArgumentNullException.ThrowIfNull(tone);

        var palette = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1"] = Colored(tone, tone.L1, tone.C1, hue, lightAdjust * 0.1, chromaAdjust * 0.25),
            ["2"] = Colored(tone, tone.L2, tone.C2, hue, lightAdjust, chromaAdjust),
            ["3"] = Colored(tone, tone.L3, tone.C3, hue, lightAdjust, chromaAdjust),
            ["4"] = Colored(tone, tone.L4, tone.C4, hue, lightAdjust, chromaAdjust),
            ["5"] = Colored(tone, tone.L5, tone.C5, hue, lightAdjust, chromaAdjust),
            ["6"] = Colored(tone, tone.L6, tone.C6, hue, lightAdjust, chromaAdjust),
            ["7"] = Colored(tone, tone.L7, tone.C7, hue, lightAdjust, chromaAdjust),
            ["8"] = Colored(tone, tone.L8, tone.C8, hue, lightAdjust, chromaAdjust),
            ["SemiTransparent"] = Colored(tone, tone.L8, tone.C8, hue, lightAdjust, chromaAdjust, tone.ASemiTransparent),
            ["Bg0"] = Colored(tone, tone.L5, tone.C5, hue, lightAdjust, chromaAdjust),
            ["Bg1"] = Colored(tone, tone.L7, tone.C7, hue, lightAdjust, chromaAdjust, tone.ASemiWhite),
        };

        return palette;
    }

    /// <summary>
    /// 明度的非对称线性调整。
    ///
    /// 正向调整把颜色往亮处推（增量随当前值增大而变小），
    /// 负向调整往暗处推；这样同一套色阶在不同调整量下都保持层次。
    /// </summary>
    public static double AdjustLinear(double value, double adjustment)
    {
        if (adjustment == 0) return value;
        value = Math.Clamp(value, 0.0, 1.0);
        adjustment = Math.Clamp(adjustment, -1.0, 1.0);
        return adjustment switch
        {
            > 0 => value + (1.0 - value) * adjustment,
            _ => value + value * adjustment
        };
    }

    private static int Gray(ToneProfile tone, double lightness, double? alpha = null)
    {
        var color = OklabColor.FromLch(lightness, 0, 0, alpha ?? 1.0);
        return color.ToArgb();
    }

    private static int Colored(
        ToneProfile tone,
        double lightness,
        double chroma,
        double hue,
        double lightAdjust,
        double chromaAdjust,
        double? alpha = null)
    {
        var color = OklabColor.FromLch(
            AdjustLinear(lightness, lightAdjust),
            AdjustLinear(chroma, chromaAdjust),
            hue,
            alpha ?? 1.0);
        return color.ToArgb();
    }
}
