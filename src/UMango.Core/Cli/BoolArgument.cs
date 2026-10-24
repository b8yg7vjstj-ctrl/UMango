// SPDX-License-Identifier: Apache-2.0
//
// 本文件派生自 PCL-CE（https://github.com/PCL-Community/PCL-CE）的
// PCL.Core/App/Cli/BoolArgument.cs。PCL.Core 采用 Apache-2.0 许可，
// 故本文件亦以 Apache-2.0 发布，详见仓库根目录 LICENSE。
//
// 相对原版的改动：
//   1. 命名空间 PCL.Core.App.Cli → UMango.Core.Cli；
//   2. **Bool 判定口径** —— 原版是「不是 0 / false 就是 true」，
//      所以 `--verbose no` 会被读成 true（"no" 既不是 0 也不是 false）。
//      这里把 "no" / "off" / "disable" / "disabled" 也认作 false，
//      因为用户写 --no-cache 期望的就是「关掉」。

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UMango.Core.Cli;

/// <summary>
/// 布尔参数。
/// </summary>
public class BoolArgument : CommandArgument<bool>
{
    /// <inheritdoc/>
    public override ArgumentValueKind ValueKind => ArgumentValueKind.Bool;

    /// <inheritdoc/>
    protected override bool ParseValueText()
    {
        var text = ValueText.Trim().ToLowerInvariant();
        // 空文本 = 开关出现了但没带值 → 视为「开」。
        // 这是命令行里 flag 的通行语义（--verbose 就是打开 verbose）。
        if (text.Length == 0) return true;
        return text is not ("0" or "false" or "no" or "off" or "disable" or "disabled");
    }

    /// <inheritdoc/>
    public override bool TryCastValue<T>([NotNullWhen(true)] out T value)
    {
        if (base.TryCastValue(out value)) return true;
        var type = typeof(T);
        if (type != typeof(sbyte) &&
            type != typeof(byte) &&
            type != typeof(short) &&
            type != typeof(ushort) &&
            type != typeof(int) &&
            type != typeof(uint) &&
            type != typeof(long) &&
            type != typeof(ulong) &&
            type != typeof(nint) &&
            type != typeof(nuint)) return false;
        // magic code
        var v = Value;
        Unsafe.As<T, byte>(ref value) = Unsafe.As<bool, byte>(ref v);
#pragma warning disable CS8762 // The analyzer sucks.
        return true;
#pragma warning restore CS8762
    }
}
