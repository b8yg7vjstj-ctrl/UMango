// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/WinRT/Interface/Windows/Data/Xml/Dom/IXmlDocumentIO.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using System;

namespace UMango.Core.Utils.WinRT.Interface.Windows.Data.Xml.Dom;

public static class IXmlDocumentIOInfo
{
    public static readonly string ActivatableClassId = "Windows.Data.Xml.Dom.XmlDocument";
    public static readonly Guid Iid = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
}

public unsafe struct IXmlDocumentIO
{
    public IXmlDocumentIOVtbl* lpVtbl;
}

public unsafe struct IXmlDocumentIOVtbl
{
    // IUnknown
    public delegate* unmanaged<void*, Guid*, void**, int> QueryInterface;
    public delegate* unmanaged<void*, uint> AddRef;
    public delegate* unmanaged<void*, uint> Release;

    // IInspectable
    public delegate* unmanaged<void*, uint*, Guid**, int> GetIids;
    public delegate* unmanaged<void*, IntPtr*, int> GetRuntimeClassName;
    public delegate* unmanaged<void*, int*, int> GetTrustLevel;

    // IXmlDocumentIO

    /// <summary>
    /// void LoadXml(string xml)
    /// </summary>
    public delegate* unmanaged<void*, IntPtr, int> LoadXml;

    /// <summary>
    /// void LoadXml(string xml, XmlLoadSettings settings)
    /// </summary>
    public delegate* unmanaged<void*, IntPtr, void*, int> LoadXmlWithSettings;

    /// <summary>
    /// IAsyncAction SaveToFileAsync(IStorageFile file)
    /// </summary>
    public delegate* unmanaged<void*, void*, void**, int> SaveToFileAsync;
}