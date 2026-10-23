// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/OS/AumidHelper.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.* → UMango.Core.*
//   · AumidHelper.Aumid / DisplayName 改为 UMango 自己的标识
//   · IconHelper.GetIconPath() 在 UMango 中不存在 → 换成空串（注册表里不写图标路径）
// ----------------------------------------------------------------------------

using Microsoft.Win32;

namespace UMango.Core.Utils.OS;

public static class AumidHelper
{
    /// <summary>
    /// UMango 的应用用户模型 ID。Windows 用它把 Toast 通知归属到"哪个应用"，
    /// 同一个 AUMID 的通知会归到一起、也能在系统设置里单独关掉。
    /// 必须是稳定的标识：改了它，用户此前对该应用的通知偏好就失效了。
    /// </summary>
    public const string Aumid = "UMango.UMango";
    
    public static bool HasAumid()
    {
        using var key = Registry.CurrentUser.OpenSubKey(string.Concat(@"Software\Classes\AppUserModelId\", Aumid));
        return key is not null;
    }
    
    public static void RegisterAumid()
    {
        // .NET 8 在正常情况下不可能返回 null，如果炸了不应该包住而是让他炸下去
        using var key = Registry.CurrentUser.CreateSubKey(string.Concat(@"Software\Classes\AppUserModelId\", Aumid));
        key.SetValue("DisplayName", "UMango");
        // IconUri 留空：UMango 是自包含发布，没有稳定的独立图标文件路径；
        // 不写这一项 Windows 会用应用自己的 exe 图标，观感正确且不会失效。
        key.SetValue("IconUri", "");
        // 用 UMango 自己的主色（Tokens.Light.Pcl.xaml 的 C3 #1370F3）而不是 PCL 的 FFDDDD。
        // Windows 在通知中心里用这个色铺图标底色，跟应用主色一致才不显得是拼凑的。
        key.SetValue("IconBackgroundColor", "FF1370F3");
    }

    public static void UnregisterAumid()
    {
        Registry.CurrentUser.DeleteSubKey(string.Concat(@"Software\Classes\AppUserModelId\", Aumid), false);
    }
}