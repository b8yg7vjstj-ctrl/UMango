// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/DecimalArgument.cs。PCL.Core 采用 Apache-2.0 许可，
// 故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.App.Cli → UMango.Core.Cli；
//   2. **区域无关** —— 原版是 `decimal.Parse(ValueText)`，走当前区域：
//      在德语区域下 "1.5" 会被读成 15，"1,5" 被读成 1.5。命令行里的
//      数字必须与区域无关，否则同一份脚本在不同机器上结果不同。
//      这里固定用 InvariantCulture，且只允许「可选符号 + 小数点」，
//      不接受千分位逗号。

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UMango.Core.Cli;

/// <summary>
/// 小数参数。
/// </summary>
public class DecimalArgument : CommandArgument<decimal>
{
    /// <inheritdoc/>
    public override ArgumentValueKind ValueKind => ArgumentValueKind.Decimal;

    /// <inheritdoc/>
    protected override decimal ParseValueText() => decimal.Parse(ValueText,
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture);

    /// <summary>参数值。</summary>
    public new decimal Value
    {
        get => base.Value;
        init => base.Value = value;
    }

    /// <inheritdoc/>
    public override bool TryCastValue<T>([NotNullWhen(true)] out T value)
    {
        if (base.TryCastValue(out value)) return true;
        var type = typeof(T);
        try
        {
            if (type == typeof(int)) Unsafe.As<T, int>(ref value) = Convert.ToInt32(Value);
            else if (type == typeof(long)) Unsafe.As<T, long>(ref value) = Convert.ToInt64(Value);
            else if (type == typeof(double)) Unsafe.As<T, double>(ref value) = Convert.ToDouble(Value);
            else if (type == typeof(float)) Unsafe.As<T, float>(ref value) = Convert.ToSingle(Value);
            else if (type == typeof(short)) Unsafe.As<T, short>(ref value) = Convert.ToInt16(Value);
            else if (type == typeof(sbyte)) Unsafe.As<T, sbyte>(ref value) = Convert.ToSByte(Value);
            else if (type == typeof(ulong)) Unsafe.As<T, ulong>(ref value) = Convert.ToUInt64(Value);
            else if (type == typeof(uint)) Unsafe.As<T, uint>(ref value) = Convert.ToUInt32(Value);
            else if (type == typeof(ushort)) Unsafe.As<T, ushort>(ref value) = Convert.ToUInt16(Value);
            else if (type == typeof(byte)) Unsafe.As<T, byte>(ref value) = Convert.ToByte(Value);
            else if (type == typeof(nint)) Unsafe.As<T, nint>(ref value) = checked((nint)Convert.ToInt64(Value));
            else if (type == typeof(nuint)) Unsafe.As<T, nuint>(ref value) = checked((nuint)Convert.ToUInt64(Value));
            else return false;
#pragma warning disable CS8762 // The analyzer sucks.
            return true;
#pragma warning restore CS8762
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        {
            return false;
        }
    }
}
