// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/HString.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using System;

namespace UMango.Core.Utils.WinRT;

/// <summary>
/// 表示一个 WinRT HSTRING 句柄的只读包装。
/// </summary>
/// <param name="Value">HSTRING 的底层指针句柄（<see cref="nint"/>）。</param>
public readonly record struct HString(IntPtr Value)
{
    public readonly IntPtr Value = Value;
    public bool IsNull => Value == IntPtr.Zero;
    public static implicit operator IntPtr(HString h) => h.Value;
    public static explicit operator HString(IntPtr ptr) => new(ptr);
}