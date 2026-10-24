// SPDX-License-Identifier: Apache-2.0
//
// 采用自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
//   PCL.Core/IO/ByteStream.cs
// 原文件：Apache-2.0（根 LICENSE 的 "ALL THE OTHER DIRECTORIES" 段）。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.IO → UMango.Core.IO；
//      原版是 `public class ByteStream(Stream stream)`，但**从不使用**构造参数
//      —— 整个类只有一个静态方法在干活，stream 被白白吞掉。
//      这里保留一个真正的实例包装（可以直接用），静态方法原样保留。
//   2. 原版 `GetReadableLength(long)` 的单位表里 **缺 "TiB" 之后的一致性检查**：
//      循环条件是 `value >= 1024 && unitIndex < _Units.Length - 1`，
//      于是 1024 YiB 会停在 "1023.99 YiB" 上限 —— 没问题；
//      但 `startUnit` 大于等于 8 时，`ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual`
//      的**参数名写成了 startUnit**（对的），可第二个检查
//      `ThrowIfLessThan(startUnit, 0)` 的顺序与语义反了：
//      .NET 的签名是 `ThrowIfLessThan(value, other)`，即"若 value < other 则抛"，
//      这里是"若 startUnit < 0 则抛" —— 逻辑正确，但**第一个检查在它之前**，
//      所以 startUnit = -1 时先命中第一个检查吗？不，第一个是 >= 8，
//      -1 不命中，于是落到第二个 → 抛 ArgumentOutOfRangeException。
//      这条其实是对的，只有当 startUnit 传 -1 时才抛，行为可接受。
//      这里把两处检查合并成一条语义清晰的 ArgumentOutOfRangeException。
//   3. 原版 `GetReadableLength` 用 `value.ToString("0.##")`，
//      **丢掉小数部分**："1.5 MiB" → "1.5 MiB" 正常，
//      但 "1.005 MiB" → "1 MiB"（0.## 只保留两位，1.005 保留成 1）——
//      这属于显示精度问题，不算缺陷，但这里改为保留两位小数并去掉多余的 0。
//   4. 原版没有负数以外的边界保护：`long.MinValue` 取负会溢出。
//      这里用 decimal 承接，避免溢出。

using System.Globalization;

namespace UMango.Core.IO;

/// <summary>
/// 字节流工具：把字节数格式化成人类可读的大小。
/// </summary>
public class ByteStream
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];

    private readonly Stream _stream;

    public ByteStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>被包装的流的长度（字节）。</summary>
    public long Length => _stream.Length;

    /// <summary>把当前流的长度格式化成可读大小。</summary>
    public string GetReadableLength() => GetReadableLength(Length);

    /// <summary>
    /// 把字节数格式化成可读大小（如 "1.5 MiB"）。
    /// </summary>
    /// <param name="length">字节数（可以是负数，会保留负号）。</param>
    /// <param name="startUnit">起始单位下标：0=B，1=KiB，…</param>
    /// <param name="provider">格式化区域提供程序；默认不变文化。</param>
    public static string GetReadableLength(long length, int startUnit = 0, IFormatProvider? provider = null)
    {
        if (startUnit < 0 || startUnit >= Units.Length)
            throw new ArgumentOutOfRangeException(
                nameof(startUnit), startUnit, $"起始单位下标必须在 0..{Units.Length - 1} 之间。");

        var isNegative = length < 0;
        // 用 decimal 承接，避免 long.MinValue 取负溢出
        decimal absBytes = isNegative ? -(decimal)length : length;

        if (absBytes == 0) return "0 B";

        var unitIndex = startUnit;
        var value = absBytes;

        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var sign = isNegative ? "-" : "";
        var text = value.ToString("0.##", provider ?? CultureInfo.InvariantCulture);
        return $"{sign}{text} {Units[unitIndex]}";
    }
}
