// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/WinRTInterop.cs
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

public static partial class WinRTInterop
{
    // roapi.h
    [LibraryImport("combase.dll")]
    private static unsafe partial int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, IntPtr* factory);
    [LibraryImport("combase.dll")]
    private static unsafe partial int RoActivateInstance(IntPtr activatableClassId, IntPtr* instance);

    public static unsafe IntPtr ActivateInstance(ReadOnlySpan<char> activatableClassId)
    {
        var instance = IntPtr.Zero;

        fixed (char* pName = activatableClassId)
        {
            HStringHeader header;
            IntPtr hstring;

            Marshal.ThrowExceptionForHR(
                WindowsCreateStringReference(
                    (ushort*)pName,
                    activatableClassId.Length,
                    (IntPtr*)&header,
                    &hstring));

            Marshal.ThrowExceptionForHR(
                RoActivateInstance(
                    hstring,
                    &instance));
        }

        return instance;
    }

    public static unsafe IntPtr GetActivationFactory(ReadOnlySpan<char> activatableClassId, Guid iid)
    {
        var factory = IntPtr.Zero;

        fixed (char* pName = activatableClassId)
        {
            HStringHeader header;
            IntPtr hstring;

            Marshal.ThrowExceptionForHR(
                WindowsCreateStringReference(
                    (ushort*)pName,
                    activatableClassId.Length,
                    (IntPtr*)&header,
                    &hstring));

            Marshal.ThrowExceptionForHR(
                RoGetActivationFactory(
                    hstring,
                    &iid,
                    &factory));
        }

        return factory;
    }
    
    // winstring.h
    [LibraryImport("combase.dll")]
    internal static unsafe partial int WindowsCreateString(ushort* sourceString, int length, IntPtr* hstring);
    [LibraryImport("combase.dll")]
    internal static unsafe partial int WindowsCreateStringReference(ushort* sourceString, int length,
        IntPtr* hstringHeader, IntPtr* hstring);
    [LibraryImport("combase.dll")]
    internal static unsafe partial int WindowsDeleteString(IntPtr hstring);
    [LibraryImport("combase.dll")]
    internal static unsafe partial char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);
    
    // hstring.h
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HStringHeader
    {
        private fixed byte _data[24];
    }
}