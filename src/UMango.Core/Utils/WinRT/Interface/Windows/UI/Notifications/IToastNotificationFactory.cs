// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/Interface/Windows/UI/Notifications/IToastNotificationFactory.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using System;

namespace UMango.Core.Utils.WinRT.Interface.Windows.UI.Notifications;

public static class IToastNotificationFactoryInfo
{
    public static readonly string ActivatableClassId = "Windows.UI.Notifications.ToastNotification";
    public static readonly Guid Iid = new("04124b20-82c6-4229-b109-fd9ed4662b53");
}

public unsafe struct IToastNotificationFactory
{
    public IToastNotificationFactoryVtbl* lpVtbl;
}

public unsafe struct IToastNotificationFactoryVtbl
{
    // IUnknown
    public delegate* unmanaged<void*, Guid*, void**, int> QueryInterface;
    public delegate* unmanaged<void*, uint> AddRef;
    public delegate* unmanaged<void*, uint> Release;

    // IInspectable
    public delegate* unmanaged<void*, uint*, Guid**, int> GetIids;
    public delegate* unmanaged<void*, IntPtr*, int> GetRuntimeClassName;
    public delegate* unmanaged<void*, int*, int> GetTrustLevel;

    // IToastNotificationFactory

    /// <summary>
    /// ToastNotification CreateToastNotification(XmlDocument content)
    /// </summary>
    public delegate* unmanaged<void*, void*, void**, int> CreateToastNotification;
}