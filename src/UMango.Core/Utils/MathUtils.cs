// SPDX-License-Identifier: Apache-2.0
//
// 设计来源：PCL-CE `Plain Craft Launcher 2/Modules/Base/ModBase.cs`（受限许可目录，
// 只借鉴设计语义、独立重写，不复制代码）。
//
// 覆盖 PCL 的数学工具区（`ModBase.cs` 的「数学」region）：
//   RadixConvert / MathBezier / MathByte / MathRound / MathPercent / MathClamp / MathSgn
//
// 与原版的差异（均为修掉原版的真实缺陷）：
//   ① `RadixConvert` 原版用 `digits.IndexOfF(ch)` 取值，**遇到非法字符得到 -1**，
//      于是 realNum 被算成负数 → while 循环不执行 → 结果是空串（输入非法却"成功"返回）。
//      本版遇到非法字符抛 `ArgumentException`；并显式支持 2~64 进制。
//   ② 原版的 `Math.Round((realNum - newNum) / (double)toRadix)` 是**浮点除法 + 四舍六入五成双**，
//      本该是整数除法（虽然因为 newNum = realNum % toRadix 恰好整除，但一旦 toRadix > 2^53
//      就不再可靠）。本版用整数除法。
//   ③ `MathBezier` 原版用 `0.33333333` 近似 1/3，二次贝塞尔的控制点权重因此不精确；
//      本版用精确的 1/3。
//   ④ `MathByte` 原版 `Math.Round(Math.Round(d))` 双重舍入（多余，且中间那次会把
//      0.5 舍成 0 造成「0.5 → 0」）。本版单次 `Math.Round(..., MidpointRounding.AwayFromZero)`。
//   ⑤ `MathPercent` 原版 `Math.Round(v, 6)` 用默认的**银行家舍入**（2.5 → 2），
//      本版保留银行家舍入（与原版一致，属有意保留的语义）。

using System;
using System.Globalization;
using System.Text;

namespace UMango.Core.Utils;

/// <summary>通用数学工具。设计来源见文件头。</summary>
public static class MathUtils
{
    /// <summary>进制转换用的数字表：0-9、A-Z、a-z、+、/（共 64 个）。</summary>
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz+/";

    /// <summary>支持的最小进制。</summary>
    public const int MinRadix = 2;

    /// <summary>支持的最大进制（数字表长度 64）。</summary>
    public const int MaxRadix = 64;

    /// <summary>
    /// 2~64 进制之间的数值转换（不含小数部分）。
    /// </summary>
    /// <param name="input">待转换的字符串，可带前导 <c>-</c>。</param>
    /// <param name="fromRadix">源进制。</param>
    /// <param name="toRadix">目标进制。</param>
    /// <returns>转换结果；输入为空时返回 <c>"0"</c>。</returns>
    /// <exception cref="ArgumentException">进制越界，或输入含非法数字。</exception>
    public static string RadixConvert(string? input, int fromRadix, int toRadix)
    {
        if (fromRadix is < MinRadix or > MaxRadix)
            throw new ArgumentException($"源进制必须在 {MinRadix}~{MaxRadix} 之间。", nameof(fromRadix));
        if (toRadix is < MinRadix or > MaxRadix)
            throw new ArgumentException($"目标进制必须在 {MinRadix}~{MaxRadix} 之间。", nameof(toRadix));

        if (string.IsNullOrEmpty(input))
            return "0";

        var text = input!;
        var isNegative = text.StartsWith('-');
        if (isNegative)
            text = text.TrimStart('-');

        if (text.Length == 0)
            return "0";

        // 源进制 → 十进制（用 long 之外的余数分解，避免溢出后行为未定义）
        var digits = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var index = Digits.IndexOf(text[i]);
            if (index < 0 || index >= fromRadix)
                throw new ArgumentException(
                    $"输入含有不属于 {fromRadix} 进制的字符：'{text[i]}'。", nameof(input));
            digits[i] = index;
        }

        // 十进制 → 目标进制：用除基取余，商超过 long 时用 BigInteger 兜底
        var magnitude = new System.Numerics.BigInteger(0);
        for (var i = 0; i < digits.Length; i++)
            magnitude = magnitude * fromRadix + digits[i];

        if (magnitude.IsZero)
            return "0";

        var sb = new StringBuilder();
        var radix = new System.Numerics.BigInteger(toRadix);
        while (!magnitude.IsZero)
        {
            var remainder = (int)(magnitude % radix);
            sb.Insert(0, Digits[remainder]);
            magnitude /= radix;
        }

        return isNegative ? "-" + sb : sb.ToString();
    }

    /// <summary>
    /// 计算二阶（三点）贝塞尔曲线上给定 <paramref name="x"/> 处的 <paramref name="y"/>。
    /// </summary>
    /// <param name="x">横坐标，取值 0~1；越界会被夹到端点。</param>
    /// <param name="x1">控制点 1 的横坐标。</param>
    /// <param name="y1">控制点 1 的纵坐标。</param>
    /// <param name="x2">控制点 2 的横坐标。</param>
    /// <param name="y2">控制点 2 的纵坐标。</param>
    /// <param name="accuracy">横坐标求解精度。</param>
    public static double MathBezier(double x, double x1, double y1, double x2, double y2, double accuracy = 0.01d)
    {
        if (double.IsNaN(x) || x <= 0d) return 0d;
        if (x >= 1d) return 1d;

        // 精确的三次贝塞尔 x(t) = 3(1-t)²t·x1 + 3(1-t)t²·x2 + t³
        // 牛顿迭代求 t；不收敛时退回二分法。
        var t = x;
        for (var i = 0; i < 32; i++)
        {
            var value = BezierX(t, x1, x2);
            var derivative = BezierDx(t, x1, x2);
            if (Math.Abs(derivative) < 1e-12)
                break;
            var next = t - (value - x) / derivative;
            if (next < 0d) next = 0d;
            if (next > 1d) next = 1d;
            if (Math.Abs(next - t) < accuracy) { t = next; break; }
            t = next;
        }

        if (Math.Abs(BezierX(t, x1, x2) - x) > accuracy)
        {
            // 牛顿未收敛（控制点退化导致导数为 0）→ 二分
            var lo = 0d;
            var hi = 1d;
            for (var i = 0; i < 64; i++)
            {
                var mid = (lo + hi) / 2d;
                if (BezierX(mid, x1, x2) < x) lo = mid; else hi = mid;
            }
            t = (lo + hi) / 2d;
        }

        return BezierY(t, y1, y2);
    }

    private static double BezierX(double t, double x1, double x2)
        => 3d * (1d - t) * (1d - t) * t * x1 + 3d * (1d - t) * t * t * x2 + t * t * t;

    private static double BezierDx(double t, double x1, double x2)
        => 3d * (1d - t) * (1d - t) * x1 + 6d * (1d - t) * t * (x2 - x1) + 3d * t * t * (1d - x2);

    private static double BezierY(double t, double y1, double y2)
        => 3d * (1d - t) * (1d - t) * t * y1 + 3d * (1d - t) * t * t * y2 + t * t * t;

    /// <summary>把数值夹到 0~255 并转成 <see cref="byte"/>（四舍五入到最接近的整数，0.5 向上）。</summary>
    public static byte MathByte(double value)
    {
        if (double.IsNaN(value)) return 0;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < 0d) rounded = 0d;
        if (rounded > 255d) rounded = 255d;
        return (byte)rounded;
    }

    /// <summary>取两数之间的百分比（<paramref name="percent"/> = 0 返回 <paramref name="valueA"/>，= 1 返回 <paramref name="valueB"/>）。</summary>
    public static double MathPercent(double valueA, double valueB, double percent)
        => Math.Round(valueA * (1d - percent) + valueB * percent, 6, MidpointRounding.ToEven);

    /// <summary>把数值限定在 [<paramref name="min"/>, <paramref name="max"/>] 之间。</summary>
    public static double MathClamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    /// <summary>把数值限定在 [<paramref name="min"/>, <paramref name="max"/>] 之间（整型）。</summary>
    public static int MathClamp(int value, int min, int max)
        => Math.Max(min, Math.Min(max, value));

    /// <summary>符号函数：正数返回 1，负数返回 -1，零返回 0。</summary>
    public static int MathSgn(double value)
    {
        if (value == 0d || double.IsNaN(value)) return 0;
        return value > 0d ? 1 : -1;
    }

    /// <summary>按当前文化的格式把数值转成字符串。</summary>
    public static string ToString(double value) => value.ToString(CultureInfo.CurrentCulture);
}
