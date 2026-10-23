// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/Interface/Windows/UI/Notifications/IToastNotifier.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using System;

namespace UMango.Core.Utils.WinRT.Interface.Windows.UI.Notifications;

public static class IToastNotifierInfo
{
    public static readonly string ActivatableClassId = "Windows.UI.Notifications.ToastNotifier";
    public static readonly Guid Iid = new("75927b93-03f3-41ec-91d3-6e5bac1b38e7");
}

public unsafe struct IToastNotifier
{
    public IToastNotifierVtbl* lpVtbl;
}

public unsafe struct IToastNotifierVtbl
{
    // IUnknown
    public delegate* unmanaged<void*, Guid*, void**, int> QueryInterface;
    public delegate* unmanaged<void*, uint> AddRef;
    public delegate* unmanaged<void*, uint> Release;

    // IInspectable
    public delegate* unmanaged<void*, uint*, Guid**, int> GetIids;
    public delegate* unmanaged<void*, IntPtr*, int> GetRuntimeClassName;
    public delegate* unmanaged<void*, int*, int> GetTrustLevel;

    // IToastNotifier
    
    /// <summary>
    /// void Show(ToastNotification notification)
    /// </summary>
    public delegate* unmanaged<void*, void*, int> Show;

    /// <summary>
    /// void Hide(ToastNotification notification)
    /// </summary>
    public delegate* unmanaged<void*, void*, int> Hide;
    
    /// <summary>
    /// NotificationSetting Setting { get; }
    /// </summary>
    public delegate* unmanaged<void*, int, int> get_Setting;
    
    /// <summary>
    /// void AddToSchedule(ScheduledToastNotification scheduledToast)
    /// </summary>
    public delegate* unmanaged<void*, void*, int> AddToSchedule;
    
    /// <summary>
    /// void RemoveFromSchedule(ScheduledToastNotification scheduledToast)
    /// </summary>
    public delegate* unmanaged<void*, void*, int> RemoveFromSchedule;
    
    /// <summary>
    /// IVectorView&lt;ScheduledToastNotification&gt; GetScheduledToastNotifications()
    /// </summary>
    public delegate* unmanaged<void*, void**, int> GetScheduledToastNotifications;
}