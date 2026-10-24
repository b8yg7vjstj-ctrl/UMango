// ----------------------------------------------------------------------------
// 采用自 PCL-Community/PCL-CE，文件：PCL.Core/Utils/OS/NetworkUtils.cs
// 原项目以 Apache License 2.0 发布（SPDX-License-Identifier: Apache-2.0）。
// 该目录（PCL.Core）整体属 Apache-2.0 层，可复用；不触发《PCL 分发有限许可》B5-B9。
// 逐字采用，仅做下述改动：
//   · 命名空间 PCL.Core.Utils.OS → UMango.Core.Utils.OS
//   · 原版在静态字段初始化时就把所有网卡地址抓一遍（类型首次访问即触发
//     NetworkInterface.GetAllNetworkInterfaces()，在某些网络环境下会抛
//     NetworkInformationException，且异常会发生在静态构造里、无法被调用方捕获）——
//     UMango 改为按需读取并吞掉异常：网卡状态会变，缓存下来的地址列表
//     很快就过期了
//   · 原版返回 IPAddress[]；这里额外提供字符串形态，便于诊断页直接展示
// ----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UMango.Core.Utils.OS;

/// <summary>本机网络信息。</summary>
public static class NetworkUtils
{
    /// <summary>
    /// 取得本机所有处于 Up 状态网卡的非回环 IP 地址。
    ///
    /// 读不到时返回空数组（不抛异常）——网卡枚举在受限环境里可能失败，
    /// 诊断页不该因为取不到地址就崩。
    /// </summary>
    public static IPAddress[] GetAllLocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x is { OperationalStatus: OperationalStatus.Up })
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address is { AddressFamily: AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 }
                             && !ua.Address.Equals(IPAddress.Any)
                             && !ua.Address.Equals(IPAddress.IPv6Any)
                             && !ua.Address.Equals(IPAddress.Loopback)
                             && !ua.Address.Equals(IPAddress.IPv6Loopback))
                .Select(ua => ua.Address)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>本机 IP 地址的文本形态（诊断页展示用）。</summary>
    public static string[] GetAllLocalAddressText() =>
        GetAllLocalAddress().Select(a => a.ToString()).ToArray();
}
