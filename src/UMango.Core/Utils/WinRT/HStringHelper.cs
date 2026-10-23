// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/HStringHelper.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using System;
using System.Runtime.InteropServices;

namespace UMango.Core.Utils.WinRT;

public class HStringHelper
{
    public static unsafe HString ToHString(string? value)
    {
        if (value is null)
        {
            return new HString(IntPtr.Zero);
        }

        IntPtr handle;
        fixed (char* lpValue = value)
        {
            Marshal.ThrowExceptionForHR(
                WinRTInterop.WindowsCreateString((ushort*)lpValue, value.Length, &handle));
        }

        return new HString(handle);
    }

    public static unsafe string ToManagedString(HString value)
    {
        if (value == IntPtr.Zero)
            return "";
        uint length;
        var buffer = WinRTInterop.WindowsGetStringRawBuffer(value, &length);
        return length != 0 ? new string(buffer, 0, (int)length) : string.Empty;
    }

    public static void DeleteHString(HString value)
    {
        if (!value.IsNull)
            WinRTInterop.WindowsDeleteString(value);
    }

    public static unsafe void WithReference(
        ReadOnlySpan<char> value,
        Action<HString> action)
    {
        fixed (char* p = value)
        {
            WinRTInterop.HStringHeader header;
            IntPtr hstring;

            Marshal.ThrowExceptionForHR(
                WinRTInterop.WindowsCreateStringReference(
                    (ushort*)p,
                    value.Length,
                    (IntPtr*)&header,
                    &hstring));

            action((HString)hstring);
        }
    }
}