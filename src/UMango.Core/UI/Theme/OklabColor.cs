// SPDX-License-Identifier: Apache-2.0
//
// 本文件参考了 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/UI/Theme/LabColor.cs。
//
// 原文采用 Apache-2.0 许可，允许逐字复用。
// 采用时做的改动：
//   1. **不引入 Wacton.Unicolour 包** —— PCL 用它做 OKLAB 色彩空间转换，
//      UMango 改为**自己实现 OKLAB 转换数学**（见下方公式出处）。
//      这样 Core 不必多一个第三方依赖，且转换逻辑可以单独测试；
//   2. 命名空间 PCL.Core.UI.Theme → UMango.Core.UI.Theme；
//   3. **剥离 WPF 依赖** —— 原版有 `FromWpfColor` / `ToWpfColor` 与
//      隐式转换运算符，UMango 改成 int ARGB 进出，
//      于是整套色彩数学能在 Core 的 net10.0 支上编译、在 macOS 上被测试；
//   4. 原版还带 `ScRgbMapping`（ScRGB 映射）那套半成品功能，
//      UMango 不迁 —— UMango 的界面是标准 sRGB，没有 ScRGB 需求；
//   5. 原版 `CatColorResource` 依赖 WPF 的资源字典定位，UMango 改成
//      纯数据的调色板（见 PaletteGenerator）。
//
// OKLAB 转换公式来自 Björn Ottosson 发表于 2020 年的公开文章
// "A perceptual color space for image processing"（MIT 许可的参考实现）
// 与其中的矩阵系数；系数本身是公开定义的常量，不构成著作权问题。

namespace UMango.Core.UI.Theme;

/// <summary>
/// OKLAB 色彩空间下的颜色。
///
/// OKLAB 的特点是**感知均匀**：色阶 L1..L8 在视觉上等距，
/// 因此按 L 值生成一整套灰阶时不会出现「暗部挤在一起、亮部拉太开」的问题。
/// 这正是 PCL 的主题体系所依赖的能力。
/// </summary>
public readonly record struct OklabColor(double L, double A, double B, double Alpha)
{
    /// <summary>从 OKLCH（明度 / 彩度 / 色相角度）创建。</summary>
    /// <param name="l">明度 0~1。</param>
    /// <param name="c">彩度，0 表示无彩（灰）。</param>
    /// <param name="h">色相角（度）。</param>
    /// <param name="alpha">透明度 0~1。</param>
    public static OklabColor FromLch(double l, double c = 0, double h = 0, double alpha = 1.0)
    {
        var radians = h * Math.PI / 180.0;
        return new OklabColor(l, c * Math.Cos(radians), c * Math.Sin(radians), alpha);
    }

    /// <summary>彩度（色度的大小，忽略色相角）。</summary>
    public double Chroma => Math.Sqrt(A * A + B * B);

    /// <summary>色相角（度，0~360）。无彩时返回 0。</summary>
    public double Hue
    {
        get
        {
            if (Chroma < 1e-9) return 0;
            var degrees = Math.Atan2(B, A) * 180.0 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }
    }

    /// <summary>从 sRGB 的 0~255 分量创建（alpha 0~1）。</summary>
    public static OklabColor FromRgb(byte r, byte g, byte b, double alpha = 1.0)
    {
        var lr = Linearize(r / 255.0);
        var lg = Linearize(g / 255.0);
        var lb = Linearize(b / 255.0);

        var l = 0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb;
        var m = 0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb;
        var s = 0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb;

        var l_ = Math.Cbrt(l);
        var m_ = Math.Cbrt(m);
        var s_ = Math.Cbrt(s);

        return new OklabColor(
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_,
            alpha);
    }

    /// <summary>从 0xAARRGGBB 整数创建。</summary>
    public static OklabColor FromArgb(int argb)
    {
        var a = (byte)(argb >> 24);
        var r = (byte)(argb >> 16);
        var g = (byte)(argb >> 8);
        var b = (byte)argb;
        return FromRgb(r, g, b, a / 255.0);
    }

    /// <summary>转换回 0xAARRGGBB 整数（alpha 取 0~255）。</summary>
    public int ToArgb()
    {
        var (r, g, b) = ToRgb();
        var a = (int)Math.Round(Math.Clamp(Alpha, 0, 1) * 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>转换回 sRGB 的 0~255 分量。</summary>
    public (byte R, byte G, byte B) ToRgb()
    {
        var l_ = L + 0.3963377774 * A + 0.2158037573 * B;
        var m_ = L - 0.1055613458 * A - 0.0638541728 * B;
        var s_ = L - 0.0894841775 * A - 1.2914855480 * B;

        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;

        var lr = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var lg = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var lb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return (Delinearize(lr), Delinearize(lg), Delinearize(lb));
    }

    /// <summary>两种颜色按比例混合（amount 为第一种颜色的权重）。</summary>
    public static OklabColor Mix(OklabColor c1, OklabColor c2, double c1Amount = 0.5)
    {
        var t = Math.Clamp(c1Amount, 0, 1);
        return new OklabColor(
            c1.L + (c2.L - c1.L) * t,
            c1.A + (c2.A - c1.A) * t,
            c1.B + (c2.B - c1.B) * t,
            c1.Alpha + (c2.Alpha - c1.Alpha) * t);
    }

    /// <summary>sRGB 传输函数的逆（gamma 解码）。</summary>
    private static double Linearize(double value)
    {
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>sRGB 传输函数（gamma 编码）。</summary>
    private static byte Delinearize(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        var encoded = clamped <= 0.0031308
            ? clamped * 12.92
            : 1.055 * Math.Pow(clamped, 1.0 / 2.4) - 0.055;
        var rounded = (int)Math.Round(encoded * 255.0, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(rounded, 0, 255);
    }
}
